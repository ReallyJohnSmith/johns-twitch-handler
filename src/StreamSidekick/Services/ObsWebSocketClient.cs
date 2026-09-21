using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Minimal OBS WebSocket 5.x client. Request IDs allow multiple reads to be in flight while a single
/// receive loop routes each response back to its caller and also consumes scene/audio events.
/// </summary>
public sealed class ObsWebSocketClient(SecureConnectionStore connectionStore) : IAsyncDisposable
{
    // OBS uses bit flags for event categories; bit 16 opts into the high-frequency volume-meter stream.
    private const int DefaultEventsWithVolumeMeters = 2047 | (1 << 16);
    private static readonly TimeSpan AudioStateLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TangiaSourceLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TangiaStatusLifetime = TimeSpan.FromSeconds(4);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _audioRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _browserSourceRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _tangiaStatusRefreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, (double Percent, DateTimeOffset SeenAt)> _inputMeters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _audioCacheLock = new();
    private IReadOnlyList<ObsAudioInput> _audioCache = [];
    private DateTimeOffset _audioCacheAt = DateTimeOffset.MinValue;
    private readonly object _browserSourceCacheLock = new();
    private ObsBrowserSourceCheck? _browserSourceCache;
    private DateTimeOffset _browserSourceCacheAt = DateTimeOffset.MinValue;
    private readonly object _tangiaCacheLock = new();
    private string? _tangiaSourceCache;
    private bool _tangiaSourceCacheReady;
    private DateTimeOffset _tangiaSourceCacheAt = DateTimeOffset.MinValue;
    private TangiaSourceSnapshot? _tangiaStatusCache;
    private DateTimeOffset _tangiaStatusCacheAt = DateTimeOffset.MinValue;
    private readonly object _tangiaFocusLock = new();
    private bool _tangiaFocusActive;
    private bool _tangiaWasMuted;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private string? _lastError;

    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public bool HasSavedConnection => connectionStore.Read().Obs is not null;
    public string? LastError => _lastError;
    // The overlay reads this in-memory flag instead of polling OBS every second. That keeps the
    // viewer banner responsive without adding WebSocket traffic during a demanding boss pull.
    public bool TangiaBossFocusActive { get { lock (_tangiaFocusLock) return _tangiaFocusActive; } }
    public event Action<string, JsonElement>? EventReceived;

    public async Task ConnectAsync(string url, string? password, CancellationToken cancellationToken = default)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        ClientWebSocket? connectingSocket = null;
        try
        {
            await DisconnectInternalAsync();
            _lastError = null;

            connectingSocket = new ClientWebSocket();
            connectingSocket.Options.AddSubProtocol("obswebsocket.json");
            await connectingSocket.ConnectAsync(new Uri(url), cancellationToken);

            var hello = await ReceiveMessageAsync(connectingSocket, cancellationToken);
            if (hello.GetProperty("op").GetInt32() != 0)
            {
                throw new InvalidOperationException("OBS did not send the expected Hello message.");
            }

            var helloData = hello.GetProperty("d");
            var identifyData = new Dictionary<string, object?>
            {
                ["rpcVersion"] = Math.Min(1, helloData.GetProperty("rpcVersion").GetInt32()),
                ["eventSubscriptions"] = DefaultEventsWithVolumeMeters
            };

            if (helloData.TryGetProperty("authentication", out var auth))
            {
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("OBS WebSocket requires a password.");
                }

                identifyData["authentication"] = CreateAuthentication(
                    password,
                    auth.GetProperty("salt").GetString() ?? "",
                    auth.GetProperty("challenge").GetString() ?? "");
            }

            await SendRawAsync(connectingSocket, new { op = 1, d = identifyData }, cancellationToken);
            var identified = await ReceiveMessageAsync(connectingSocket, cancellationToken);
            if (identified.GetProperty("op").GetInt32() != 2)
            {
                throw new InvalidOperationException("OBS rejected the WebSocket identification request.");
            }

            _socket = connectingSocket;
            connectingSocket = null; // Ownership transfers to the service after a successful handshake.
            _receiveCancellation = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_socket, _receiveCancellation.Token);
            connectionStore.SaveObs(new StoredObsConnection(url, password));
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            connectingSocket?.Dispose();
            if (_socket is not null)
            {
                _socket.Dispose();
                _socket = null;
            }
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task ConnectSavedAsync(CancellationToken cancellationToken = default)
    {
        var saved = connectionStore.Read().Obs
            ?? throw new InvalidOperationException("No saved OBS connection is available.");
        await ConnectAsync(saved.Url, saved.Password, cancellationToken);
    }

    public async Task<ObsSceneSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return new ObsSceneSnapshot(false, null, [], null, null, null, Error: _lastError);
        }

        try
        {
            // These reads are independent; issuing them together cuts snapshot latency on a busy stream PC.
            var sceneTask = RequestAsync("GetSceneList", null, cancellationToken);
            var streamTask = RequestAsync("GetStreamStatus", null, cancellationToken);
            var recordTask = RequestAsync("GetRecordStatus", null, cancellationToken);
            var statsTask = RequestAsync("GetStats", null, cancellationToken);
            var replayTask = TryGetReplayStatusAsync();
            await Task.WhenAll(sceneTask, streamTask, recordTask, statsTask);
            var sceneData = await sceneTask;
            var streamData = await streamTask;
            var recordData = await recordTask;
            var statsData = await statsTask;
            var replayData = await replayTask;

            var scenes = sceneData.GetProperty("scenes")
                .EnumerateArray()
                .Select(scene => scene.GetProperty("sceneName").GetString() ?? "")
                .Where(name => name.Length > 0)
                .ToList();

            return new ObsSceneSnapshot(
                true,
                sceneData.GetProperty("currentProgramSceneName").GetString(),
                scenes,
                streamData.GetProperty("outputActive").GetBoolean(),
                recordData.GetProperty("outputActive").GetBoolean(),
                replayData?.GetProperty("outputActive").GetBoolean(),
                statsData.GetProperty("cpuUsage").GetDouble(),
                statsData.GetProperty("activeFps").GetDouble(),
                Percent(
                    statsData.GetProperty("renderSkippedFrames").GetDouble(),
                    statsData.GetProperty("renderTotalFrames").GetDouble()),
                Percent(
                    statsData.GetProperty("outputSkippedFrames").GetDouble(),
                    statsData.GetProperty("outputTotalFrames").GetDouble()),
                streamData.GetProperty("outputCongestion").GetDouble());
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            return new ObsSceneSnapshot(IsConnected, null, [], null, null, null, Error: exception.Message);
        }

        static double Percent(double skipped, double total) =>
            total <= 0 ? 0 : Math.Round(skipped / total * 100, 2);

        async Task<JsonElement?> TryGetReplayStatusAsync()
        {
            try { return await RequestAsync("GetReplayBufferStatus", null, cancellationToken); }
            catch { return null; } // Replay Buffer may be disabled; every other status remains useful.
        }
    }

    public Task SetCurrentSceneAsync(string sceneName, CancellationToken cancellationToken = default) =>
        RequestAsync("SetCurrentProgramScene", new { sceneName }, cancellationToken);

    public Task SaveReplayBufferAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("SaveReplayBuffer", null, cancellationToken);

    public async Task<ObsAudioMixerSnapshot> GetAudioMixerAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return new ObsAudioMixerSnapshot(false, [], _lastError);
        }

        await _audioRefreshGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<ObsAudioInput> cached;
            DateTimeOffset cachedAt;
            lock (_audioCacheLock)
            {
                cached = _audioCache;
                cachedAt = _audioCacheAt;
            }

            // OBS already pushes live meter and mute/volume change events. The slower full refresh is
            // only a safety net for source-list changes, so the one-second UI never fans out into a
            // burst of WebSocket requests on every tick.
            if (cached.Count == 0 || DateTimeOffset.UtcNow - cachedAt > AudioStateLifetime)
            {
                var listData = await RequestAsync("GetInputList", null, cancellationToken);
                var candidates = listData.GetProperty("inputs").EnumerateArray()
                    .Select(input => (
                        Name: input.GetProperty("inputName").GetString() ?? "",
                        Kind: input.TryGetProperty("unversionedInputKind", out var kind)
                            ? kind.GetString() ?? ""
                            : input.GetProperty("inputKind").GetString() ?? ""))
                    .Where(input => input.Name.Length > 0)
                    .ToList();

                var reads = candidates.Select(async input =>
                {
                    try
                    {
                        var volumeTask = RequestAsync("GetInputVolume", new { inputName = input.Name }, cancellationToken);
                        var muteTask = RequestAsync("GetInputMute", new { inputName = input.Name }, cancellationToken);
                        await Task.WhenAll(volumeTask, muteTask);
                        var volume = await volumeTask;
                        var mute = await muteTask;
                        return new ObsAudioInput(
                            input.Name,
                            input.Kind,
                            Math.Round(volume.GetProperty("inputVolumeDb").GetDouble(), 1),
                            mute.GetProperty("inputMuted").GetBoolean(),
                            MeterFor(input.Name));
                    }
                    catch
                    {
                        return null;
                    }
                });

                cached = (await Task.WhenAll(reads))
                    .Where(input => input is not null)
                    .Select(input => input!)
                    .OrderBy(input => input.InputName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                lock (_audioCacheLock)
                {
                    _audioCache = cached;
                    _audioCacheAt = DateTimeOffset.UtcNow;
                }
            }

            return new ObsAudioMixerSnapshot(true, cached.Select(input => input with
            {
                MeterPercent = MeterFor(input.InputName)
            }).ToList());
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            return new ObsAudioMixerSnapshot(IsConnected, [], exception.Message);
        }
        finally
        {
            _audioRefreshGate.Release();
        }
    }

    public async Task SetInputVolumeAsync(string inputName, double volumeDb, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputName)) throw new ArgumentException("An OBS audio source is required.");
        if (!double.IsFinite(volumeDb) || volumeDb is < -100 or > 26) throw new ArgumentOutOfRangeException(nameof(volumeDb));
        var rounded = Math.Round(volumeDb, 1);
        await RequestAsync("SetInputVolume", new { inputName, inputVolumeDb = rounded }, cancellationToken);
        UpdateCachedAudioInput(inputName, input => input with { VolumeDb = rounded });
        InvalidateTangiaStatus();
    }

    public async Task SetInputMuteAsync(string inputName, bool muted, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputName)) throw new ArgumentException("An OBS audio source is required.");
        await RequestAsync("SetInputMute", new { inputName, inputMuted = muted }, cancellationToken);
        UpdateCachedAudioInput(inputName, input => input with { Muted = muted });
        InvalidateTangiaStatus();
    }

    public async Task<ObsMicrophoneCheck> GetMicrophoneStatusAsync(CancellationToken cancellationToken = default)
    {
        var mixer = await GetAudioMixerAsync(cancellationToken);
        if (!mixer.Connected)
            return new ObsMicrophoneCheck(false, false, true, "Connect OBS before checking the microphone.");

        // Prefer the standard OBS global microphone name, then explicit microphone names, then a
        // platform audio-input capture. This avoids mistaking Desktop Audio for the microphone.
        var microphone = mixer.Inputs
            .OrderBy(input => MicrophoneRank(input))
            .FirstOrDefault(input => MicrophoneRank(input) < 3);
        if (microphone is null)
            return new ObsMicrophoneCheck(true, false, true, "No microphone source was found in the OBS mixer.");

        return new ObsMicrophoneCheck(
            true,
            true,
            microphone.Muted,
            microphone.Muted
                ? $"{microphone.InputName} is muted in OBS."
                : $"{microphone.InputName} is unmuted and ready.",
            microphone.InputName);

        static int MicrophoneRank(ObsAudioInput input)
        {
            if (input.InputName.Equals("Mic/Aux", StringComparison.OrdinalIgnoreCase)) return 0;
            if (input.InputName.Contains("microphone", StringComparison.OrdinalIgnoreCase)
                || input.InputName.StartsWith("mic", StringComparison.OrdinalIgnoreCase)
                || input.InputName.Contains(" mic", StringComparison.OrdinalIgnoreCase)) return 1;
            return input.InputKind.Contains("input_capture", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        }
    }

    public async Task<ObsBrowserSourceCheck> GetMusicOverlayStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            return new ObsBrowserSourceCheck(false, "Connect OBS before checking the song-request overlay.");

        lock (_browserSourceCacheLock)
        {
            if (_browserSourceCache is not null
                && DateTimeOffset.UtcNow - _browserSourceCacheAt < TimeSpan.FromSeconds(20))
                return _browserSourceCache;
        }

        await _browserSourceRefreshGate.WaitAsync(cancellationToken);
        try
        {
            // Recheck after taking the gate because another preflight request may have refreshed it.
            lock (_browserSourceCacheLock)
            {
                if (_browserSourceCache is not null
                    && DateTimeOffset.UtcNow - _browserSourceCacheAt < TimeSpan.FromSeconds(20))
                    return _browserSourceCache;
            }

            var list = await RequestAsync("GetInputList", new { inputKind = "browser_source" }, cancellationToken);
            foreach (var input in list.GetProperty("inputs").EnumerateArray())
            {
                var inputName = input.GetProperty("inputName").GetString();
                if (string.IsNullOrWhiteSpace(inputName)) continue;
                var settings = await RequestAsync("GetInputSettings", new { inputName }, cancellationToken);
                if (!settings.TryGetProperty("inputSettings", out var inputSettings)
                    || !inputSettings.TryGetProperty("url", out var urlElement)) continue;
                var url = urlElement.GetString() ?? "";
                if (!url.Contains("/music-overlay.html", StringComparison.OrdinalIgnoreCase)) continue;
                if (!url.Contains("127.0.0.1:5274", StringComparison.OrdinalIgnoreCase)
                    && !url.Contains("localhost:5274", StringComparison.OrdinalIgnoreCase)) continue;

                return Cache(new ObsBrowserSourceCheck(
                    true,
                    $"OBS Browser Source “{inputName}” is configured. Confirm its eye icon is on in your gameplay scene.",
                    inputName));
            }

            return Cache(new ObsBrowserSourceCheck(
                false,
                "Add the music overlay URL to OBS as a Browser Source."));
        }
        catch (Exception exception)
        {
            return Cache(new ObsBrowserSourceCheck(false, $"OBS could not check Browser Sources: {exception.Message}"));
        }
        finally
        {
            _browserSourceRefreshGate.Release();
        }

        ObsBrowserSourceCheck Cache(ObsBrowserSourceCheck value)
        {
            lock (_browserSourceCacheLock)
            {
                _browserSourceCache = value;
                _browserSourceCacheAt = DateTimeOffset.UtcNow;
            }
            return value;
        }
    }

    /// <summary>Finds Tangia by either its source name or its Browser Source URL.</summary>
    public async Task<TangiaSourceSnapshot> GetTangiaStatusAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!IsConnected)
            return new TangiaSourceSnapshot(false, false, "Connect OBS before checking Tangia.");

        lock (_tangiaCacheLock)
        {
            if (!forceRefresh && _tangiaStatusCache is not null
                && DateTimeOffset.UtcNow - _tangiaStatusCacheAt < TangiaStatusLifetime)
                return _tangiaStatusCache;
        }

        await _tangiaStatusRefreshGate.WaitAsync(cancellationToken);
        try
        {
            // Preflight and the Tangia card often refresh together. Rechecking here lets both callers
            // share one OBS read instead of duplicating volume, mute, scene, and visibility requests.
            lock (_tangiaCacheLock)
            {
                if (!forceRefresh && _tangiaStatusCache is not null
                    && DateTimeOffset.UtcNow - _tangiaStatusCacheAt < TangiaStatusLifetime)
                    return _tangiaStatusCache;
            }

            var sourceName = await FindTangiaSourceAsync(cancellationToken);
            if (sourceName is null)
                return CacheTangiaStatus(new TangiaSourceSnapshot(true, false, "Add the Tangia overlay to OBS as a Browser Source named Tangia."));

            var volumeTask = RequestAsync("GetInputVolume", new { inputName = sourceName }, cancellationToken);
            var muteTask = RequestAsync("GetInputMute", new { inputName = sourceName }, cancellationToken);
            await Task.WhenAll(volumeTask, muteTask);
            var volume = await volumeTask;
            var mute = await muteTask;

            bool? visible = null;
            try
            {
                var scene = await RequestAsync("GetCurrentProgramScene", null, cancellationToken);
                var sceneName = scene.GetProperty("currentProgramSceneName").GetString();
                if (!string.IsNullOrWhiteSpace(sceneName))
                {
                    var item = await RequestAsync("GetSceneItemId", new { sceneName, sourceName }, cancellationToken);
                    var enabled = await RequestAsync("GetSceneItemEnabled", new
                    {
                        sceneName,
                        sceneItemId = item.GetProperty("sceneItemId").GetInt32()
                    }, cancellationToken);
                    visible = enabled.GetProperty("sceneItemEnabled").GetBoolean();
                }
            }
            catch
            {
                // A source nested inside an OBS group is still controllable for audio, but its
                // scene visibility cannot be read without walking the group's child scene.
            }

            bool focus;
            lock (_tangiaFocusLock) focus = _tangiaFocusActive;
            var muted = mute.GetProperty("inputMuted").GetBoolean();
            return CacheTangiaStatus(new TangiaSourceSnapshot(
                true,
                true,
                focus ? $"Boss Focus is muting “{sourceName}”." : $"Tangia source “{sourceName}” is ready.",
                sourceName,
                muted,
                Math.Round(volume.GetProperty("inputVolumeDb").GetDouble(), 1),
                visible,
                focus));
        }
        catch (Exception exception)
        {
            return CacheTangiaStatus(new TangiaSourceSnapshot(true, false, $"OBS could not inspect Tangia: {exception.Message}"));
        }
        finally
        {
            _tangiaStatusRefreshGate.Release();
        }
    }

    public async Task<TangiaSourceSnapshot> SetTangiaMuteAsync(bool muted, CancellationToken cancellationToken = default)
    {
        var sourceName = await RequireTangiaSourceAsync(cancellationToken);
        await SetInputMuteAsync(sourceName, muted, cancellationToken);
        return await GetTangiaStatusAsync(cancellationToken, true);
    }

    public async Task<TangiaSourceSnapshot> SetTangiaVolumeAsync(double volumeDb, CancellationToken cancellationToken = default)
    {
        var sourceName = await RequireTangiaSourceAsync(cancellationToken);
        await SetInputVolumeAsync(sourceName, volumeDb, cancellationToken);
        return await GetTangiaStatusAsync(cancellationToken, true);
    }

    public async Task<TangiaSourceSnapshot> ReloadTangiaAsync(CancellationToken cancellationToken = default)
    {
        var sourceName = await RequireTangiaSourceAsync(cancellationToken);
        try
        {
            await RequestAsync("PressInputPropertiesButton", new { inputName = sourceName, propertyName = "refreshnocache" }, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"OBS found Tangia but could not reload it. Open the source properties and click Refresh cache. {exception.Message}");
        }
        InvalidateTangiaStatus();
        return await GetTangiaStatusAsync(cancellationToken, true);
    }

    public async Task<TangiaSourceSnapshot> SetTangiaVisibilityAsync(bool visible, CancellationToken cancellationToken = default)
    {
        var sourceName = await RequireTangiaSourceAsync(cancellationToken);
        var scene = await RequestAsync("GetCurrentProgramScene", null, cancellationToken);
        var sceneName = scene.GetProperty("currentProgramSceneName").GetString()
            ?? throw new InvalidOperationException("OBS did not report the current scene.");
        try
        {
            var item = await RequestAsync("GetSceneItemId", new { sceneName, sourceName }, cancellationToken);
            await RequestAsync("SetSceneItemEnabled", new
            {
                sceneName,
                sceneItemId = item.GetProperty("sceneItemId").GetInt32(),
                sceneItemEnabled = visible
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Tangia is not a top-level source in the current OBS scene. Show or hide it in OBS, or move it out of its source group. {exception.Message}");
        }
        InvalidateTangiaStatus();
        return await GetTangiaStatusAsync(cancellationToken, true);
    }

    public async Task<TangiaSourceSnapshot> SetTangiaBossFocusAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var sourceName = await RequireTangiaSourceAsync(cancellationToken);
        if (enabled)
        {
            bool alreadyActive;
            lock (_tangiaFocusLock) alreadyActive = _tangiaFocusActive;
            if (alreadyActive) return await GetTangiaStatusAsync(cancellationToken);

            var mute = await RequestAsync("GetInputMute", new { inputName = sourceName }, cancellationToken);
            var wasMuted = mute.GetProperty("inputMuted").GetBoolean();
            await SetInputMuteAsync(sourceName, true, cancellationToken);
            lock (_tangiaFocusLock)
            {
                _tangiaWasMuted = wasMuted;
                _tangiaFocusActive = true;
            }
        }
        else
        {
            bool restoreMute;
            bool wasActive;
            lock (_tangiaFocusLock)
            {
                wasActive = _tangiaFocusActive;
                restoreMute = _tangiaWasMuted;
            }
            if (!wasActive) return await GetTangiaStatusAsync(cancellationToken);
            await SetInputMuteAsync(sourceName, restoreMute, cancellationToken);
            lock (_tangiaFocusLock) _tangiaFocusActive = false;
        }
        InvalidateTangiaStatus();
        return await GetTangiaStatusAsync(cancellationToken, true);
    }

    private async Task<string> RequireTangiaSourceAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new InvalidOperationException("Connect OBS before controlling Tangia.");
        return await FindTangiaSourceAsync(cancellationToken)
            ?? throw new InvalidOperationException("Tangia was not found. Add its overlay to OBS as a Browser Source and name the source Tangia.");
    }

    private async Task<string?> FindTangiaSourceAsync(CancellationToken cancellationToken)
    {
        lock (_tangiaCacheLock)
        {
            if (_tangiaSourceCacheReady
                && DateTimeOffset.UtcNow - _tangiaSourceCacheAt < TangiaSourceLifetime)
                return _tangiaSourceCache;
        }

        var list = await RequestAsync("GetInputList", new { inputKind = "browser_source" }, cancellationToken);
        foreach (var input in list.GetProperty("inputs").EnumerateArray())
        {
            var inputName = input.GetProperty("inputName").GetString();
            if (string.IsNullOrWhiteSpace(inputName)) continue;
            if (inputName.Contains("Tangia", StringComparison.OrdinalIgnoreCase)) return CacheTangiaSource(inputName);
            try
            {
                var settings = await RequestAsync("GetInputSettings", new { inputName }, cancellationToken);
                var url = settings.TryGetProperty("inputSettings", out var inputSettings)
                    && inputSettings.TryGetProperty("url", out var urlElement)
                        ? urlElement.GetString() ?? ""
                        : "";
                if (url.Contains("tangia.co", StringComparison.OrdinalIgnoreCase)) return CacheTangiaSource(inputName);
            }
            catch
            {
                // Continue checking other browser sources if one source's settings are unavailable.
            }
        }
        return CacheTangiaSource(null);
    }

    public async Task<JsonElement> RequestAsync(
        string requestType,
        object? requestData = null,
        CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("OBS is not connected.");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register before sending so a very fast OBS response cannot arrive before its waiter exists.
        _pending[requestId] = completion;

        try
        {
            var data = new Dictionary<string, object?>
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId
            };
            if (requestData is not null)
            {
                data["requestData"] = requestData;
            }

            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await SendRawAsync(socket, new { op = 6, d = data }, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }

            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, cancellationToken);
                var op = message.GetProperty("op").GetInt32();
                var data = message.GetProperty("d");

                if (op == 7)
                {
                    var requestId = data.GetProperty("requestId").GetString();
                    if (requestId is not null && _pending.TryRemove(requestId, out var completion))
                    {
                        var status = data.GetProperty("requestStatus");
                        if (status.GetProperty("result").GetBoolean())
                        {
                            var responseData = data.TryGetProperty("responseData", out var response)
                                ? response.Clone()
                                : JsonDocument.Parse("{}").RootElement.Clone();
                            completion.TrySetResult(responseData);
                        }
                        else
                        {
                            var comment = status.TryGetProperty("comment", out var commentElement)
                                ? commentElement.GetString()
                                : "OBS request failed.";
                            completion.TrySetException(new InvalidOperationException(comment));
                        }
                    }
                }
                else if (op == 5)
                {
                    var eventType = data.GetProperty("eventType").GetString() ?? "ObsEvent";
                    var eventData = data.TryGetProperty("eventData", out var payload)
                        ? payload.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    UpdateAudioMeters(eventType, eventData);
                    UpdateCachesFromEvent(eventType, eventData);
                    EventReceived?.Invoke(eventType, eventData);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static async Task<JsonElement> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"OBS closed the connection: {socket.CloseStatusDescription}");
            }
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static Task SendRawAsync(
        ClientWebSocket socket,
        object message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string CreateAuthentication(string password, string salt, string challenge)
    {
        var secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(password + salt));
        var secret = Convert.ToBase64String(secretHash);
        var authenticationHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge));
        return Convert.ToBase64String(authenticationHash);
    }

    private void UpdateAudioMeters(string eventType, JsonElement eventData)
    {
        if (!eventType.Equals("InputVolumeMeters", StringComparison.Ordinal) ||
            !eventData.TryGetProperty("inputs", out var inputs)) return;

        foreach (var input in inputs.EnumerateArray())
        {
            if (!input.TryGetProperty("inputName", out var nameElement) ||
                !input.TryGetProperty("inputLevelsMul", out var levels)) continue;
            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var peak = 0d;
            foreach (var channel in levels.EnumerateArray())
            {
                foreach (var level in channel.EnumerateArray())
                {
                    if (level.TryGetDouble(out var value)) peak = Math.Max(peak, value);
                }
            }
            var db = peak <= 0 ? -60 : 20 * Math.Log10(peak);
            var percent = Math.Round(Math.Clamp((db + 60) / 60 * 100, 0, 100), 1);
            _inputMeters[name] = (percent, DateTimeOffset.UtcNow);
        }
    }

    private double MeterFor(string inputName) =>
        _inputMeters.TryGetValue(inputName, out var meter) && DateTimeOffset.UtcNow - meter.SeenAt < TimeSpan.FromMilliseconds(750)
            ? meter.Percent
            : 0;

    private void UpdateCachesFromEvent(string eventType, JsonElement eventData)
    {
        if (eventType.Equals("InputVolumeChanged", StringComparison.Ordinal)
            && TryReadInputName(eventData, out var volumeName)
            && eventData.TryGetProperty("inputVolumeDb", out var volumeDb))
        {
            UpdateCachedAudioInput(volumeName, input => input with
            {
                VolumeDb = Math.Round(volumeDb.GetDouble(), 1)
            });
        }
        else if (eventType.Equals("InputMuteStateChanged", StringComparison.Ordinal)
                 && TryReadInputName(eventData, out var muteName)
                 && eventData.TryGetProperty("inputMuted", out var muted))
        {
            UpdateCachedAudioInput(muteName, input => input with { Muted = muted.GetBoolean() });
        }

        if (eventType is "InputCreated" or "InputRemoved" or "InputNameChanged" or "CurrentSceneCollectionChanged")
        {
            // Source topology changed. Expire discovery caches, but let the next dashboard request do
            // the actual work so the OBS receive loop stays quick and never blocks event delivery.
            lock (_audioCacheLock) _audioCacheAt = DateTimeOffset.MinValue;
            lock (_browserSourceCacheLock) _browserSourceCacheAt = DateTimeOffset.MinValue;
            lock (_tangiaCacheLock)
            {
                _tangiaSourceCacheReady = false;
                _tangiaStatusCacheAt = DateTimeOffset.MinValue;
            }
        }
        else if (eventType is "InputVolumeChanged" or "InputMuteStateChanged"
                 or "CurrentProgramSceneChanged" or "SceneItemEnableStateChanged")
        {
            InvalidateTangiaStatus();
        }

        static bool TryReadInputName(JsonElement data, out string name)
        {
            name = data.TryGetProperty("inputName", out var element) ? element.GetString() ?? "" : "";
            return name.Length > 0;
        }
    }

    private void UpdateCachedAudioInput(string inputName, Func<ObsAudioInput, ObsAudioInput> update)
    {
        lock (_audioCacheLock)
        {
            var copy = _audioCache.ToList();
            var index = copy.FindIndex(input =>
                input.InputName.Equals(inputName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            copy[index] = update(copy[index]);
            _audioCache = copy;
        }
    }

    private string? CacheTangiaSource(string? sourceName)
    {
        lock (_tangiaCacheLock)
        {
            _tangiaSourceCache = sourceName;
            _tangiaSourceCacheReady = true;
            _tangiaSourceCacheAt = DateTimeOffset.UtcNow;
        }
        return sourceName;
    }

    private TangiaSourceSnapshot CacheTangiaStatus(TangiaSourceSnapshot snapshot)
    {
        lock (_tangiaCacheLock)
        {
            _tangiaStatusCache = snapshot;
            _tangiaStatusCacheAt = DateTimeOffset.UtcNow;
        }
        return snapshot;
    }

    private void InvalidateTangiaStatus()
    {
        lock (_tangiaCacheLock) _tangiaStatusCacheAt = DateTimeOffset.MinValue;
    }

    private async Task DisconnectInternalAsync()
    {
        _receiveCancellation?.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Reconnecting", CancellationToken.None);
            }
            catch
            {
                socket.Abort();
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch
            {
            }
        }

        _receiveCancellation?.Dispose();
        _socket?.Dispose();
        _receiveCancellation = null;
        _receiveTask = null;
        _socket = null;
        _inputMeters.Clear();
        lock (_audioCacheLock)
        {
            _audioCache = [];
            _audioCacheAt = DateTimeOffset.MinValue;
        }
        lock (_browserSourceCacheLock)
        {
            _browserSourceCache = null;
            _browserSourceCacheAt = DateTimeOffset.MinValue;
        }
        lock (_tangiaCacheLock)
        {
            _tangiaSourceCache = null;
            _tangiaSourceCacheReady = false;
            _tangiaStatusCache = null;
            _tangiaStatusCacheAt = DateTimeOffset.MinValue;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await DisconnectInternalAsync();
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
            _sendGate.Dispose();
            _audioRefreshGate.Dispose();
            _browserSourceRefreshGate.Dispose();
            _tangiaStatusRefreshGate.Dispose();
        }
    }
}
