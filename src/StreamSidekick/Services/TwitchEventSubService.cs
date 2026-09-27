using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Maintains Twitch's local EventSub WebSocket session and converts relevant notifications into a
/// bounded activity feed. Subscriptions are recreated only for a fresh session; Twitch reconnect URLs
/// preserve the existing subscriptions.
/// </summary>
public sealed class TwitchEventSubService(IHttpClientFactory httpClientFactory, ILogger<TwitchEventSubService> logger)
    : IAsyncDisposable
{
    public event Action<TwitchRewardRedemption>? RewardRedeemed;
    public event Action<TwitchAdBreak>? AdBreakStarted;
    public event Action<TwitchChatMessage>? ChatMessageReceived;
    public event Action<TwitchChatMessage>? ChatCommandReceived;
    public event Action<StreamAlert>? AlertReceived;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<StreamAlert> _alerts = new();
    private readonly ConcurrentQueue<TwitchChatMessage> _chatMessages = new();
    private readonly List<string> _setupWarnings = [];
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private string _status = "Twitch alerts are not configured.";

    public AlertFeedSnapshot GetSnapshot()
    {
        lock (_setupWarnings)
        {
            return new AlertFeedSnapshot(
                _socket?.State == WebSocketState.Open,
                _status,
                _setupWarnings.ToList(),
                _alerts.Reverse().Take(50).ToList());
        }
    }

    public TwitchChatFeedSnapshot GetChatSnapshot() => new(
        _socket?.State == WebSocketState.Open,
        _status,
        _chatMessages.Reverse().Take(80).Reverse().ToList());

    public async Task ConnectAsync(TwitchConfigurationRequest configuration, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        ClientWebSocket? connectingSocket = null;
        try
        {
            await DisconnectInternalAsync();
            lock (_setupWarnings) _setupWarnings.Clear();
            _status = "Connecting to Twitch EventSub…";

            connectingSocket = new ClientWebSocket();
            await connectingSocket.ConnectAsync(
                new Uri("wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30"),
                cancellationToken);

            var welcome = await ReceiveMessageAsync(connectingSocket, cancellationToken);
            var messageType = welcome.GetProperty("metadata").GetProperty("message_type").GetString();
            if (messageType != "session_welcome")
            {
                throw new InvalidOperationException("Twitch did not send an EventSub welcome message.");
            }

            var sessionId = welcome.GetProperty("payload").GetProperty("session").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Twitch EventSub did not provide a session ID.");

            var subscriptions = BuildSubscriptions(configuration.BroadcasterId, sessionId);
            var results = await Task.WhenAll(subscriptions.Select(subscription =>
                SubscribeAsync(configuration, subscription, cancellationToken)));

            lock (_setupWarnings)
            {
                _setupWarnings.AddRange(results.Where(result => result is not null).Select(result => result!));
            }

            _socket = connectingSocket;
            connectingSocket = null; // Ownership transfers only after all subscription attempts finish.
            _receiveCancellation = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_socket, _receiveCancellation.Token);
            _status = _setupWarnings.Count == 0
                ? "Live Twitch alerts connected."
                : "Connected; some alert types need additional Twitch permissions.";
        }
        catch (Exception exception)
        {
            connectingSocket?.Dispose();
            _status = $"Twitch alerts unavailable: {exception.Message}";
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void AddTestAlert()
    {
        Enqueue(new StreamAlert(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            "test",
            "Test alert received",
            "The alert feed is ready for stream events."));
    }

    public void AddLocalAlert(string type, string title, string detail, string? userName = null)
    {
        Enqueue(new StreamAlert(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            type,
            title,
            detail,
            userName));
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisconnectInternalAsync();
            _status = "Twitch alerts are not configured.";
            lock (_setupWarnings) _setupWarnings.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<EventSubscriptionRequest> BuildSubscriptions(string broadcasterId, string sessionId)
    {
        // Each Twitch event family has its own scope. Failures become setup warnings instead of blocking
        // unrelated events, so a missing subscription permission does not disable raids or stream status.
        var broadcaster = new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId };
        return
        [
            new("channel.follow", "2", new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = broadcasterId,
                ["moderator_user_id"] = broadcasterId
            }, sessionId),
            new("channel.subscribe", "1", broadcaster, sessionId),
            new("channel.subscription.gift", "1", broadcaster, sessionId),
            new("channel.subscription.message", "1", broadcaster, sessionId),
            new("channel.cheer", "1", broadcaster, sessionId),
            new("channel.raid", "1", new Dictionary<string, string>
            {
                ["to_broadcaster_user_id"] = broadcasterId
            }, sessionId),
            new("channel.channel_points_custom_reward_redemption.add", "1", broadcaster, sessionId),
            new("channel.chat.message", "1", new Dictionary<string, string>
            {
                ["broadcaster_user_id"] = broadcasterId,
                ["user_id"] = broadcasterId
            }, sessionId),
            new("channel.ad_break.begin", "1", broadcaster, sessionId),
            new("stream.online", "1", broadcaster, sessionId),
            new("stream.offline", "1", broadcaster, sessionId)
        ];
    }

    private async Task<string?> SubscribeAsync(
        TwitchConfigurationRequest configuration,
        EventSubscriptionRequest subscription,
        CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration.AccessToken);
        client.DefaultRequestHeaders.Add("Client-Id", configuration.ClientId);
        using var response = await client.PostAsJsonAsync(
            "https://api.twitch.tv/helix/eventsub/subscriptions",
            new
            {
                type = subscription.Type,
                version = subscription.Version,
                condition = subscription.Condition,
                transport = new { method = "websocket", session_id = subscription.SessionId }
            },
            cancellationToken);

        if (response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = TryReadError(body) ?? $"HTTP {(int)response.StatusCode}";
        return $"{FriendlyType(subscription.Type)}: {detail}";
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken);
                var metadata = message.GetProperty("metadata");
                var messageType = metadata.GetProperty("message_type").GetString();
                if (messageType == "notification")
                {
                    var payload = message.GetProperty("payload");
                    var type = payload.GetProperty("subscription").GetProperty("type").GetString() ?? "event";
                    var eventData = payload.GetProperty("event");
                    if (type == "channel.channel_points_custom_reward_redemption.add")
                    {
                        var redemption = ParseRewardRedemption(eventData);
                        if (redemption is not null) RewardRedeemed?.Invoke(redemption);
                    }
                    else if (type == "channel.ad_break.begin")
                    {
                        var adBreak = ParseAdBreak(eventData);
                        if (adBreak is not null) AdBreakStarted?.Invoke(adBreak);
                    }
                    else if (type == "channel.chat.message")
                    {
                        var chatMessage = ParseChatMessage(eventData);
                        // Command handling receives broadcaster messages too, making commands testable
                        // from John's account. Bot replies do not start with !, so they cannot loop.
                        if (chatMessage is not null)
                        {
                            _chatMessages.Enqueue(chatMessage);
                            while (_chatMessages.Count > 100) _chatMessages.TryDequeue(out _);
                            ChatCommandReceived?.Invoke(chatMessage);
                        }
                        // John's own messages include bot replies sent by this same Twitch account.
                        // Excluding them prevents a feedback chime for the Handler's confirmations.
                        if (chatMessage is { IsBroadcaster: false }) ChatMessageReceived?.Invoke(chatMessage);
                    }
                    var alert = ParseAlert(type, eventData);
                    if (alert is not null) Enqueue(alert);
                }
                else if (messageType == "revocation")
                {
                    var type = message.GetProperty("payload").GetProperty("subscription").GetProperty("type").GetString() ?? "event";
                    Enqueue(new StreamAlert(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "warning", "Twitch permission revoked", FriendlyType(type)));
                }
                else if (messageType == "session_reconnect")
                {
                    var reconnectUrl = message.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString();
                    if (!string.IsNullOrWhiteSpace(reconnectUrl))
                    {
                        _status = "Reconnecting Twitch alerts…";
                        await ReconnectAsync(socket, reconnectUrl, cancellationToken);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Twitch EventSub receive loop stopped.");
            _status = $"Twitch alerts disconnected: {exception.Message}";
        }
    }

    private async Task ReconnectAsync(ClientWebSocket previousSocket, string reconnectUrl, CancellationToken cancellationToken)
    {
        var replacement = new ClientWebSocket();
        await replacement.ConnectAsync(new Uri(reconnectUrl), cancellationToken);
        var welcome = await ReceiveMessageAsync(replacement, cancellationToken);
        var messageType = welcome.GetProperty("metadata").GetProperty("message_type").GetString();
        if (messageType != "session_welcome")
        {
            replacement.Dispose();
            throw new InvalidOperationException("Twitch EventSub reconnect did not complete.");
        }

        _socket = replacement;
        previousSocket.Dispose();
        _status = "Live Twitch alerts connected.";
        await ReceiveLoopAsync(replacement, cancellationToken);
    }

    private static StreamAlert? ParseAlert(string type, JsonElement data)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var userName = Read(data, "user_name") ?? Read(data, "from_broadcaster_user_name");
        return type switch
        {
            "channel.follow" => new(id, now, "follow", $"{userName ?? "Someone"} followed", "New follower", userName),
            "channel.subscribe" => new(id, now, "subscription", $"{userName ?? "Someone"} subscribed", $"Tier {FormatTier(Read(data, "tier"))}", userName),
            "channel.subscription.gift" => new(id, now, "gift", $"{userName ?? "Anonymous"} gifted subs", $"{ReadNumber(data, "total")} gift subscription(s)", userName),
            "channel.subscription.message" => new(id, now, "resub", $"{userName ?? "Someone"} resubscribed", $"{ReadNumber(data, "cumulative_months")} total months", userName),
            "channel.cheer" => new(id, now, "cheer", $"{userName ?? "Anonymous"} cheered", $"{ReadNumber(data, "bits")} Bits", userName),
            "channel.raid" => new(id, now, "raid", $"Raid from {userName ?? "another channel"}", $"{ReadNumber(data, "viewers")} viewers", userName),
            "channel.channel_points_custom_reward_redemption.add" => new(id, now, "reward", $"{userName ?? "Someone"} redeemed a reward", ReadNested(data, "reward", "title") ?? "Channel points reward", userName),
            "channel.ad_break.begin" => new(id, now, "ad", "Ad break started", $"{ReadFlexibleNumber(data, "duration_seconds")} seconds", userName),
            "stream.online" => new(id, now, "live", "Stream is live", "Twitch detected the broadcast."),
            "stream.offline" => new(id, now, "offline", "Stream ended", "Twitch detected the broadcast ending."),
            _ => null
        };
    }

    private static TwitchRewardRedemption? ParseRewardRedemption(JsonElement data)
    {
        var rewardId = ReadNested(data, "reward", "id");
        var rewardTitle = ReadNested(data, "reward", "title");
        if (string.IsNullOrWhiteSpace(rewardId) || string.IsNullOrWhiteSpace(rewardTitle)) return null;
        return new TwitchRewardRedemption(
            Read(data, "id") ?? Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            rewardId,
            rewardTitle,
            Read(data, "user_name") ?? "Viewer",
            Read(data, "user_input") ?? "");
    }

    private static TwitchAdBreak? ParseAdBreak(JsonElement data)
    {
        var duration = ReadFlexibleNumber(data, "duration_seconds");
        if (duration <= 0) return null;
        var startedAt = Read(data, "started_at") is { } rawStart
            && DateTimeOffset.TryParse(rawStart, out var parsedStart)
                ? parsedStart
                : DateTimeOffset.UtcNow;
        var automatic = data.TryGetProperty("is_automatic", out var automaticElement)
            && (automaticElement.ValueKind == JsonValueKind.True
                || automaticElement.ValueKind == JsonValueKind.String
                    && bool.TryParse(automaticElement.GetString(), out var parsedAutomatic)
                    && parsedAutomatic);
        return new TwitchAdBreak(
            startedAt,
            duration,
            automatic,
            Read(data, "requester_user_name"));
    }

    private static TwitchChatMessage? ParseChatMessage(JsonElement data)
    {
        var messageId = Read(data, "message_id");
        var chatterId = Read(data, "chatter_user_id");
        var broadcasterId = Read(data, "broadcaster_user_id");
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(chatterId)) return null;
        var text = data.TryGetProperty("message", out var message)
            ? Read(message, "text") ?? ""
            : "";
        return new TwitchChatMessage(
            messageId,
            DateTimeOffset.UtcNow,
            chatterId,
            Read(data, "chatter_user_name") ?? "Viewer",
            text,
            chatterId.Equals(broadcasterId, StringComparison.Ordinal));
    }

    private void Enqueue(StreamAlert alert)
    {
        _alerts.Enqueue(alert);
        AlertReceived?.Invoke(alert);
        // Bound memory use during long-running streams while retaining enough recent context for the UI.
        while (_alerts.Count > 100) _alerts.TryDequeue(out _);
    }

    private async Task DisconnectInternalAsync()
    {
        _receiveCancellation?.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None); }
            catch { socket.Abort(); }
        }
        if (_receiveTask is not null)
        {
            try { await _receiveTask; } catch { }
        }
        _receiveCancellation?.Dispose();
        _socket?.Dispose();
        _receiveCancellation = null;
        _receiveTask = null;
        _socket = null;
    }

    private static async Task<JsonElement> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(socket.CloseStatusDescription ?? "Twitch closed EventSub.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch { return null; }
    }

    private static string? Read(JsonElement data, string property) =>
        data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int ReadNumber(JsonElement data, string property) =>
        data.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static int ReadFlexibleNumber(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var value)) return 0;
        if (value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static string? ReadNested(JsonElement data, string parent, string property) =>
        data.TryGetProperty(parent, out var nested) ? Read(nested, property) : null;

    private static string FormatTier(string? tier) => tier switch { "1000" => "1", "2000" => "2", "3000" => "3", _ => tier ?? "?" };

    private static string FriendlyType(string type) => type switch
    {
        "channel.follow" => "Follows",
        "channel.subscribe" => "Subscriptions",
        "channel.subscription.gift" => "Gift subscriptions",
        "channel.subscription.message" => "Resubscriptions",
        "channel.cheer" => "Cheers",
        "channel.raid" => "Raids",
        "channel.channel_points_custom_reward_redemption.add" => "Channel point rewards",
        "channel.chat.message" => "Channel chat",
        "channel.ad_break.begin" => "Ad breaks",
        "stream.online" or "stream.offline" => "Stream status",
        _ => type
    };

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _gate.Dispose();
    }

    private sealed record EventSubscriptionRequest(
        string Type,
        string Version,
        Dictionary<string, string> Condition,
        string SessionId);
}

public sealed record TwitchRewardRedemption(
    string Id,
    DateTimeOffset At,
    string RewardId,
    string RewardTitle,
    string UserName,
    string UserInput);

public sealed record TwitchChatMessage(
    string Id,
    DateTimeOffset At,
    string UserId,
    string UserName,
    string Text,
    bool IsBroadcaster);

public sealed record TwitchChatFeedSnapshot(
    bool Connected,
    string Status,
    IReadOnlyList<TwitchChatMessage> Messages);
