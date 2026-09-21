using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamSidekick.Services;

public sealed record SpotifyAutomationOptions(string RewardTitle, bool AutoQueue, bool AllowExplicit);
public sealed record SpotifyTrack(string Uri, string Id, string Title, string Artist, string Album, int DurationMs, bool Explicit, string SpotifyUrl);
public sealed record SpotifyPlayback(
    bool IsPlaying,
    string DeviceName,
    int VolumePercent,
    int ProgressMs,
    SpotifyTrack? Track,
    string? Requester = null);
public sealed record SpotifyQueuedTrack(SpotifyTrack Track, string? Requester, bool ViewerRequest);
public sealed record SpotifyConnectSnapshot(
    bool Connected,
    string Status,
    SpotifyPlayback? Playback,
    IReadOnlyList<SpotifyQueuedTrack> Queue,
    SpotifyAutomationOptions Options,
    IReadOnlyList<SpotifySongRequest> Requests);
public sealed record SpotifyQueueTrackRequest(string Uri, string? Requester = null, string? SourceText = null);
public sealed record SpotifyRequestDecision(string RequestId);
public sealed record SpotifyTestRequest(string Query, string? Requester = null);
public sealed record SpotifyAuthorizationRequest(string? ClientId = null);
public sealed record SpotifyRewardResult(bool Success, string UserName, string Message);

/// <summary>A local audit entry that lets the streamer see which request matched and how it ended.</summary>
public sealed class SpotifySongRequest
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Requester { get; init; } = "Streamer";
    public string SourceText { get; init; } = "";
    public SpotifyTrack? Track { get; init; }
    public string Status { get; set; } = "pending";
    public string? Note { get; set; }
}

/// <summary>
/// Owns Spotify's PKCE authorization, playback commands, and Twitch-reward-to-track matching.
/// Tokens are encrypted for the current Windows account so John's Twitch Handler can reconnect.
/// </summary>
public sealed class SpotifyService
{
    private const string CallbackUrl = "http://127.0.0.1:5274/api/spotify/callback";
    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<SpotifyService> logger;
    private readonly SecureConnectionStore connectionStore;
    private readonly ConnectionDiagnosticsLog diagnostics;
    private readonly string? _packagedClientId;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _tokenRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _snapshotRefreshGate = new(1, 1);
    private readonly List<SpotifySongRequest> _requests = [];
    private string? _clientId;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt;
    private string? _oauthState;
    private string? _codeVerifier;
    private string _status = "Spotify is not connected.";
    private string? _lastLoggedStatusError;
    private SpotifyAutomationOptions _options = new("Song Request", true, true);
    private SpotifyConnectSnapshot? _snapshotCache;
    private DateTimeOffset _snapshotCacheAt = DateTimeOffset.MinValue;

    public SpotifyService(
        IHttpClientFactory httpClientFactory,
        ILogger<SpotifyService> logger,
        SecureConnectionStore connectionStore,
        ConnectionDiagnosticsLog diagnostics,
        IConfiguration configuration)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        this.connectionStore = connectionStore;
        this.diagnostics = diagnostics;
        _packagedClientId = NormalizeClientId(configuration["Integrations:Spotify:ClientId"]);

        var saved = connectionStore.Read().Spotify;
        if (saved is null) return;
        _clientId = saved.ClientId;
        _accessToken = saved.AccessToken;
        _refreshToken = saved.RefreshToken;
        _expiresAt = saved.ExpiresAt;
        _status = "Restored Spotify connection; checking playback…";
    }

    public bool ApplicationConfigured
    {
        get { lock (_sync) return !string.IsNullOrWhiteSpace(_packagedClientId) || !string.IsNullOrWhiteSpace(_clientId); }
    }

    public bool IsConnected
    {
        get { lock (_sync) return !string.IsNullOrWhiteSpace(_accessToken); }
    }

    public string BeginAuthorization(string? clientId = null)
    {
        // Spotify is optional. When enabled, a packaged public ID removes the developer-dashboard
        // step for testers while PKCE keeps the flow free of embedded client secrets.
        var trimmed = NormalizeClientId(clientId) ?? _packagedClientId ?? _clientId ?? "";
        if (trimmed.Length is < 16 or > 128 || trimmed.Any(character => !char.IsLetterOrDigit(character)))
            throw new InvalidOperationException("This build is missing the Spotify application ID. Spotify can stay disabled, or the app owner can add the ID to appsettings.json.");

        // PKCE proves that the local app which started sign-in is the one completing it; no client secret is stored.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        lock (_sync)
        {
            _clientId = trimmed;
            _codeVerifier = verifier;
            _oauthState = state;
            _status = "Waiting for Spotify authorization…";
        }

        var scopes = Uri.EscapeDataString("user-read-playback-state user-modify-playback-state");
        return "https://accounts.spotify.com/authorize" +
            $"?client_id={Uri.EscapeDataString(trimmed)}&response_type=code&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
            $"&scope={scopes}&code_challenge_method=S256&code_challenge={Uri.EscapeDataString(challenge)}&state={Uri.EscapeDataString(state)}";
    }

    public async Task CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken = default)
    {
        string clientId;
        string verifier;
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_oauthState) || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(_oauthState), Encoding.UTF8.GetBytes(state)))
                throw new InvalidOperationException("Spotify returned an invalid authorization state. Start the connection again.");
            clientId = _clientId ?? throw new InvalidOperationException("Spotify Client ID is missing.");
            verifier = _codeVerifier ?? throw new InvalidOperationException("Spotify authorization verifier is missing.");
        }

        using var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = CallbackUrl,
            ["code_verifier"] = verifier
        }), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadSpotifyError(body) ?? $"Spotify token exchange failed ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(body);
        lock (_sync)
        {
            _accessToken = json.RootElement.GetProperty("access_token").GetString();
            _refreshToken = json.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
            _oauthState = null;
            _codeVerifier = null;
            _status = "Spotify connected. Start playback on a Spotify device to accept requests.";
            InvalidateSnapshotUnsafe();
            SaveConnectionUnsafe();
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            _accessToken = null;
            _refreshToken = null;
            _expiresAt = default;
            _oauthState = null;
            _codeVerifier = null;
            _status = "Spotify is not connected.";
            InvalidateSnapshotUnsafe();
            connectionStore.ClearSpotify();
        }
    }

    public SpotifyAutomationOptions SetOptions(SpotifyAutomationOptions options)
    {
        var rewardTitle = options.RewardTitle?.Trim() ?? "";
        if (rewardTitle.Length is < 1 or > 80) throw new ArgumentException("Reward title must be 1–80 characters.");
        lock (_sync)
        {
            _options = options with { RewardTitle = rewardTitle };
            InvalidateSnapshotUnsafe();
            return _options;
        }
    }

    public async Task<SpotifyConnectSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_snapshotCache is not null
                && DateTimeOffset.UtcNow - _snapshotCacheAt < TimeSpan.FromSeconds(3))
                return _snapshotCache;
        }

        await _snapshotRefreshGate.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
            {
                if (_snapshotCache is not null
                    && DateTimeOffset.UtcNow - _snapshotCacheAt < TimeSpan.FromSeconds(3))
                    return _snapshotCache;
            }

            // The dashboard and OBS overlay usually ask together. One short shared snapshot prevents
            // duplicate playback and queue calls without making the visible queue feel stale.
            var snapshot = await BuildSnapshotAsync(cancellationToken);
            lock (_sync)
            {
                _snapshotCache = snapshot;
                _snapshotCacheAt = DateTimeOffset.UtcNow;
            }
            return snapshot;
        }
        finally
        {
            _snapshotRefreshGate.Release();
        }
    }

    private async Task<SpotifyConnectSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        SpotifyPlayback? playback = null;
        IReadOnlyList<SpotifyQueuedTrack> queue = [];
        bool connected;
        lock (_sync) connected = !string.IsNullOrWhiteSpace(_accessToken);
        if (connected)
        {
            try
            {
                playback = await GetPlaybackAsync(cancellationToken);
                // Spotify returns only track metadata. Reattach the requester from our local audit
                // trail so the OBS overlay can credit the viewer whose song is currently playing.
                if (playback?.Track is not null)
                {
                    lock (_sync)
                    {
                        var playingRequest = _requests
                            .Where(request => request.Status == "queued" && request.Track?.Uri == playback.Track.Uri)
                            .OrderBy(request => request.At)
                            .FirstOrDefault();
                        playback = playback with { Requester = playingRequest?.Requester };
                    }
                }
                // The queue has no actionable meaning without a Spotify Connect device, so skip the extra API call.
                if (playback is not null) queue = await GetQueueAsync(playback.Track, cancellationToken);
                lock (_sync) _status = playback is null
                    ? "Connected; open Spotify and start playback on a device."
                    : playback.IsPlaying ? $"Playing on {playback.DeviceName}." : $"Paused on {playback.DeviceName}.";
                _lastLoggedStatusError = null;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Spotify playback status failed.");
                lock (_sync) _status = exception.Message;
                if (!string.Equals(_lastLoggedStatusError, exception.Message, StringComparison.Ordinal))
                {
                    diagnostics.Record("Spotify", "warning", $"Playback check failed: {exception.GetBaseException().Message}");
                    _lastLoggedStatusError = exception.Message;
                }
            }
        }
        lock (_sync) return new SpotifyConnectSnapshot(connected, _status, playback, queue, _options, _requests.OrderByDescending(item => item.At).Take(40).ToList());
    }

    public async Task SkipAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(HttpMethod.Post, "https://api.spotify.com/v1/me/player/next", null, cancellationToken, expectJson: false);
        lock (_sync)
        {
            _status = "Skipped to the next Spotify track.";
            InvalidateSnapshotUnsafe();
        }
    }

    /// <summary>
    /// Fetches only current playback for chat commands. This avoids downloading the full queue when
    /// a viewer merely asks for the clickable link to the song that is already playing.
    /// </summary>
    public async Task<SpotifyPlayback?> GetNowPlayingAsync(CancellationToken cancellationToken = default) =>
        await GetPlaybackAsync(cancellationToken);

    public async Task<IReadOnlyList<SpotifyTrack>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var search = query?.Trim() ?? "";
        if (search.Length is < 2 or > 200) throw new ArgumentException("Enter at least two characters to search Spotify.");
        var json = await SendAsync(HttpMethod.Get, $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(search)}&type=track&limit=6", null, cancellationToken);
        if (!json.TryGetProperty("tracks", out var tracks) || !tracks.TryGetProperty("items", out var items)) return [];
        return items.EnumerateArray().Select(ParseTrack).Where(track => track is not null).Select(track => track!).ToList();
    }

    public async Task<SpotifySongRequest> QueueTrackAsync(SpotifyQueueTrackRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Uri.StartsWith("spotify:track:", StringComparison.Ordinal)) throw new ArgumentException("A Spotify track URI is required.");
        var track = await GetTrackAsync(request.Uri["spotify:track:".Length..], cancellationToken);
        var songRequest = new SpotifySongRequest
        {
            Requester = string.IsNullOrWhiteSpace(request.Requester) ? "Streamer" : request.Requester.Trim(),
            SourceText = request.SourceText?.Trim() ?? track.Title,
            Track = track,
            Status = "pending"
        };
        AddRequest(songRequest);
        await QueueRequestAsync(songRequest, cancellationToken);
        return songRequest;
    }

    public async Task<SpotifySongRequest> ApproveAsync(string requestId, CancellationToken cancellationToken = default)
    {
        SpotifySongRequest request;
        lock (_sync)
        {
            request = _requests.FirstOrDefault(item => item.Id == requestId)
                ?? throw new FileNotFoundException("Song request not found.");
            if (request.Status != "pending") throw new InvalidOperationException("Only pending requests can be approved.");
            // Reserve the request before awaiting Spotify so a double-click cannot queue the same track twice.
            request.Status = "processing";
        }
        await QueueRequestAsync(request, cancellationToken);
        return request;
    }

    public SpotifySongRequest Reject(string requestId)
    {
        lock (_sync)
        {
            var request = _requests.FirstOrDefault(item => item.Id == requestId)
                ?? throw new FileNotFoundException("Song request not found.");
            if (request.Status != "pending") throw new InvalidOperationException("Only pending requests can be rejected.");
            request.Status = "rejected";
            request.Note = "Rejected by the streamer.";
            InvalidateSnapshotUnsafe();
            return request;
        }
    }

    public async Task<SpotifyRewardResult?> ReceiveRewardAsync(TwitchRewardRedemption redemption, CancellationToken cancellationToken = default)
    {
        SpotifyAutomationOptions options;
        lock (_sync) options = _options;
        if (!redemption.RewardTitle.Equals(options.RewardTitle, StringComparison.OrdinalIgnoreCase)) return null;

        var request = new SpotifySongRequest
        {
            Requester = redemption.UserName,
            SourceText = redemption.UserInput,
            Status = "pending"
        };
        try
        {
            var matches = await SearchAsync(redemption.UserInput, cancellationToken);
            var track = matches.FirstOrDefault(candidate => options.AllowExplicit || !candidate.Explicit);
            request = new SpotifySongRequest
            {
                Id = request.Id,
                At = request.At,
                Requester = request.Requester,
                SourceText = request.SourceText,
                Track = track,
                Status = track is null ? "failed" : "pending",
                Note = track is null ? "No permitted Spotify track matched the request." : null
            };
            AddRequest(request);
            if (track is null)
            {
                diagnostics.Record("Spotify", "warning", $"No Spotify track matched {redemption.UserName}'s request: {redemption.UserInput}");
                return FailedReward(redemption.UserName, "I couldn’t find a matching Spotify track. Try “song title - artist”.");
            }
            if (options.AutoQueue)
            {
                await QueueRequestAsync(request, cancellationToken);
                diagnostics.Record("Spotify", "success", $"Queued “{track.Title}” by {track.Artist} for {redemption.UserName}.");
                return new SpotifyRewardResult(
                    true,
                    redemption.UserName,
                    MusicChatMessage($"@{redemption.UserName} your request “{track.Title}” by {track.Artist} was added to the Spotify queue!"));
            }
            return new SpotifyRewardResult(
                true,
                redemption.UserName,
                MusicChatMessage($"@{redemption.UserName} I found “{track.Title}” by {track.Artist}; it is waiting for streamer approval."));
        }
        catch (Exception exception)
        {
            request.Status = "failed";
            request.Note = exception.Message;
            AddRequest(request);
            diagnostics.Record("Spotify", "error", $"Song request for {redemption.UserName} failed: {exception.GetBaseException().Message}");
            return FailedReward(redemption.UserName, FriendlyQueueFailure(exception));
        }
    }

    public async Task ReceiveTestRequestAsync(SpotifyTestRequest request, CancellationToken cancellationToken = default)
    {
        string rewardTitle;
        lock (_sync) rewardTitle = _options.RewardTitle;
        _ = await ReceiveRewardAsync(new TwitchRewardRedemption(
            Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "test", rewardTitle,
            string.IsNullOrWhiteSpace(request.Requester) ? "Test viewer" : request.Requester.Trim(), request.Query.Trim()), cancellationToken);
    }

    private static SpotifyRewardResult FailedReward(string userName, string reason) => new(
        false,
        userName,
        MusicChatMessage($"@{userName} {reason}"));

    private static string FriendlyQueueFailure(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        if (message.Contains("NO_ACTIVE_DEVICE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("active device", StringComparison.OrdinalIgnoreCase))
            return "Spotify has no active playback device. The streamer needs to open Spotify and start playback, then try again.";
        if (message.Contains("PREMIUM", StringComparison.OrdinalIgnoreCase))
            return "Spotify queue control requires an active Premium account.";
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate", StringComparison.OrdinalIgnoreCase))
            return "Spotify is temporarily limiting requests. Please wait a moment and try again.";
        return "Spotify found the song but couldn’t queue it. The streamer can check John's Twitch Handler connection log for the exact reason.";
    }

    private static string MusicChatMessage(string value)
    {
        const string prefix = "John's Twitch Handler: ";
        var message = prefix + value;
        return message.Length <= 480 ? message : message[..477] + "…";
    }

    private async Task QueueRequestAsync(SpotifySongRequest request, CancellationToken cancellationToken)
    {
        if (request.Track is null) throw new InvalidOperationException("This request has no matched Spotify track.");
        try
        {
            await SendAsync(HttpMethod.Post, $"https://api.spotify.com/v1/me/player/queue?uri={Uri.EscapeDataString(request.Track.Uri)}", null, cancellationToken, expectJson: false);
            lock (_sync)
            {
                request.Status = "queued";
                request.Note = "Added to the active Spotify device.";
                InvalidateSnapshotUnsafe();
            }
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                request.Status = "failed";
                request.Note = exception.Message;
                InvalidateSnapshotUnsafe();
            }
            throw;
        }
    }

    private void AddRequest(SpotifySongRequest request)
    {
        lock (_sync)
        {
            if (_requests.All(item => item.Id != request.Id)) _requests.Add(request);
            while (_requests.Count > 100) _requests.RemoveAt(0);
            InvalidateSnapshotUnsafe();
        }
    }

    private void InvalidateSnapshotUnsafe()
    {
        // Call only while holding _sync. The cached object is immutable, so readers can safely
        // finish rendering an older copy while the next request refreshes it.
        _snapshotCache = null;
        _snapshotCacheAt = DateTimeOffset.MinValue;
    }

    private async Task<SpotifyPlayback?> GetPlaybackAsync(CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player", null, cancellationToken, allowEmpty: true);
        if (json.ValueKind == JsonValueKind.Undefined) return null;
        var device = json.GetProperty("device");
        SpotifyTrack? track = null;
        if (json.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object) track = ParseTrack(item);
        return new SpotifyPlayback(
            json.TryGetProperty("is_playing", out var playing) && playing.GetBoolean(),
            device.TryGetProperty("name", out var name) ? name.GetString() ?? "Spotify device" : "Spotify device",
            device.TryGetProperty("volume_percent", out var volume) && volume.TryGetInt32(out var percent) ? percent : 0,
            json.TryGetProperty("progress_ms", out var progress) && progress.TryGetInt32(out var milliseconds) ? milliseconds : 0,
            track);
    }

    private async Task<IReadOnlyList<SpotifyQueuedTrack>> GetQueueAsync(SpotifyTrack? currentlyPlaying, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "https://api.spotify.com/v1/me/player/queue", null, cancellationToken);
        if (!json.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array) return [];

        List<SpotifySongRequest> unmatchedRequests;
        lock (_sync)
        {
            unmatchedRequests = _requests
                .Where(request => request.Status == "queued" && request.Track is not null)
                .OrderBy(request => request.At)
                .ToList();
        }

        if (currentlyPlaying is not null)
        {
            var currentRequestIndex = unmatchedRequests.FindIndex(request => request.Track!.Uri == currentlyPlaying.Uri);
            if (currentRequestIndex >= 0) unmatchedRequests.RemoveAt(currentRequestIndex);
        }

        // Spotify does not preserve our requester metadata. Match queued URIs in request order to rebuild it for the UI.
        var result = new List<SpotifyQueuedTrack>();
        foreach (var item in queue.EnumerateArray().Take(20))
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "track") continue;
            var track = ParseTrack(item);
            if (track is null) continue;
            var requestIndex = unmatchedRequests.FindIndex(request => request.Track!.Uri == track.Uri);
            if (requestIndex < 0)
            {
                result.Add(new SpotifyQueuedTrack(track, null, false));
                continue;
            }

            var request = unmatchedRequests[requestIndex];
            unmatchedRequests.RemoveAt(requestIndex);
            result.Add(new SpotifyQueuedTrack(track, request.Requester, true));
        }
        return result;
    }

    private async Task<SpotifyTrack> GetTrackAsync(string id, CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, $"https://api.spotify.com/v1/tracks/{Uri.EscapeDataString(id)}", null, cancellationToken);
        return ParseTrack(json) ?? throw new InvalidOperationException("Spotify did not return the requested track.");
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken,
        bool allowEmpty = false,
        bool expectJson = true)
    {
        await EnsureTokenAsync(cancellationToken);
        string token;
        lock (_sync) token = _accessToken ?? throw new InvalidOperationException("Connect Spotify first.");
        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null && method != HttpMethod.Get) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        if (allowEmpty && response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadSpotifyError(text) ?? $"Spotify request failed ({(int)response.StatusCode}).");
        // Queue/skip endpoints have no response model. Some Spotify edges return an unexpected text
        // body with HTTP 200 instead of the documented empty response; success must remain success.
        if (!expectJson) return default;
        if (string.IsNullOrWhiteSpace(text)) return JsonDocument.Parse("{}").RootElement.Clone();
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        // Several dashboard endpoints can arrive together. Serialize refreshes so a rotating refresh token is used once.
        await _tokenRefreshGate.WaitAsync(cancellationToken);
        try
        {
            string? refreshToken;
            string? clientId;
            lock (_sync)
            {
                // Recheck after acquiring the gate; another caller may already have refreshed the token.
                if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
                refreshToken = _refreshToken;
                clientId = _clientId;
            }
            if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Connect Spotify first.");

            using var client = httpClientFactory.CreateClient();
            using var response = await client.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadSpotifyError(body) ?? "Spotify token refresh failed.");
            using var json = JsonDocument.Parse(body);
            lock (_sync)
            {
                // Disconnect may have happened while the network call was in flight; never silently reconnect afterward.
                if (!string.Equals(_refreshToken, refreshToken, StringComparison.Ordinal)
                    || !string.Equals(_clientId, clientId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Spotify was disconnected while refreshing its token.");
                _accessToken = json.RootElement.GetProperty("access_token").GetString();
                _expiresAt = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
                if (json.RootElement.TryGetProperty("refresh_token", out var refresh)) _refreshToken = refresh.GetString();
                SaveConnectionUnsafe();
            }
        }
        finally
        {
            _tokenRefreshGate.Release();
        }
    }

    private static SpotifyTrack? ParseTrack(JsonElement item)
    {
        if (!item.TryGetProperty("uri", out var uri) || !item.TryGetProperty("id", out var id)) return null;
        var artists = item.TryGetProperty("artists", out var artistList)
            ? string.Join(", ", artistList.EnumerateArray().Select(artist => artist.GetProperty("name").GetString()).Where(name => !string.IsNullOrWhiteSpace(name)))
            : "Unknown artist";
        var album = item.TryGetProperty("album", out var albumData) && albumData.TryGetProperty("name", out var albumName)
            ? albumName.GetString() ?? "" : "";
        var spotifyUrl = item.TryGetProperty("external_urls", out var urls) && urls.TryGetProperty("spotify", out var link)
            ? link.GetString() ?? "" : "";
        return new SpotifyTrack(
            uri.GetString() ?? "",
            id.GetString() ?? "",
            item.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown track" : "Unknown track",
            artists,
            album,
            item.TryGetProperty("duration_ms", out var duration) && duration.TryGetInt32(out var ms) ? ms : 0,
            item.TryGetProperty("explicit", out var explicitValue) && explicitValue.GetBoolean(),
            spotifyUrl);
    }

    private static string? ReadSpotifyError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("error", out var error)) return null;
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            return error.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch { return null; }
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? NormalizeClientId(string? clientId)
    {
        var trimmed = clientId?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private void SaveConnectionUnsafe()
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_accessToken)) return;
        connectionStore.SaveSpotify(new StoredSpotifyConnection(
            _clientId,
            _accessToken,
            _refreshToken,
            _expiresAt));
    }
}
