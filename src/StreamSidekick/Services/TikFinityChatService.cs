using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Reads TikTok chat events from TikFinity's local desktop WebSocket. TikFinity owns the TikTok
/// connection; the Handler never receives a TikTok password, session cookie, or stream key.
/// </summary>
public sealed class TikFinityChatService(
    IConfiguration configuration,
    ILogger<TikFinityChatService> logger) : BackgroundService
{
    private static readonly Uri EventApi = new("ws://127.0.0.1:21213/");
    private readonly object _sync = new();
    private readonly Queue<TikFinityChatMessage> _messages = new();
    private CancellationTokenSource? _connectionCancellation;
    private bool _enabled = configuration.GetValue("Integrations:TikFinity:EnabledByDefault", false);
    private bool _connected;
    private string _status = "Optional integration is turned off.";

    public TikFinitySnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new TikFinitySnapshot(_enabled, _connected, _status, _messages.ToList());
        }
    }

    public TikFinitySnapshot SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            _enabled = enabled;
            _connected = false;
            _status = enabled
                ? "Waiting for the TikFinity desktop app."
                : "Optional integration is turned off.";
            if (!enabled) _messages.Clear();
            _connectionCancellation?.Cancel();
            return new TikFinitySnapshot(_enabled, _connected, _status, _messages.ToList());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!IsEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_sync) _connectionCancellation = connectionCancellation;
                try
                {
                    await ReadConnectionAsync(connectionCancellation.Token);
                }
                catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
                {
                    // Turning the optional feature off deliberately closes the current socket.
                }
                catch (Exception exception)
                {
                    SetConnectionState(false, "Open TikFinity and start its local Event API, then the Handler will reconnect automatically.");
                    logger.LogDebug(exception, "TikFinity's local chat socket is not available yet.");
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_connectionCancellation, connectionCancellation))
                            _connectionCancellation = null;
                    }
                }

                if (IsEnabled()) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal application shutdown should not be reported as a failed integration.
        }
    }

    private bool IsEnabled()
    {
        lock (_sync) return _enabled;
    }

    private async Task ReadConnectionAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(EventApi, cancellationToken);
        SetConnectionState(true, "Connected to TikFinity's local chat feed.");

        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SetConnectionState(false, "TikFinity closed its local chat feed. Reconnecting shortly.");
                    return;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;
            TryRecordChat(Encoding.UTF8.GetString(message.ToArray()));
        }
    }

    private void TryRecordChat(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var eventName)
                || !string.Equals(eventName.GetString(), "chat", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("data", out var data)) return;

            var user = data.TryGetProperty("user", out var userElement) ? userElement : default;
            var userName = ReadString(data, "uniqueId") ?? ReadString(user, "uniqueId") ?? "tiktok-viewer";
            var displayName = ReadString(data, "nickname") ?? ReadString(user, "nickname") ?? userName;
            var text = ReadString(data, "comment") ?? ReadString(data, "text");
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_sync)
            {
                _messages.Enqueue(new TikFinityChatMessage(DateTimeOffset.UtcNow, userName, displayName, text.Trim()));
                while (_messages.Count > 150) _messages.Dequeue();
            }
        }
        catch (JsonException exception)
        {
            logger.LogDebug(exception, "TikFinity sent an unreadable local event.");
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private void SetConnectionState(bool connected, string status)
    {
        lock (_sync)
        {
            _connected = connected;
            _status = status;
        }
    }
}

public sealed record TikFinityChatMessage(DateTimeOffset At, string UserName, string DisplayName, string Text);
public sealed record TikFinitySnapshot(bool Enabled, bool Connected, string Status, IReadOnlyList<TikFinityChatMessage> Messages);
public sealed record TikFinityEnabledRequest(bool Enabled);
