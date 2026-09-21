using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Plays a short OBS Monitor Only tone for viewer chat. Playback is blocked whenever an unmuted
/// desktop/output capture could recapture the monitoring device and leak the tone to viewers.
/// </summary>
public sealed class PrivateChatChimeService
{
    private const string InputName = "John's Twitch Handler · Private Chat Chime";
    private readonly ObsWebSocketClient obs;
    private readonly ILogger<PrivateChatChimeService> logger;
    private readonly SemaphoreSlim _playGate = new(1, 1);
    private readonly object _sync = new();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "private-chat-chime.json");
    private bool _enabled = true;
    private double _volumeDb = -30;
    private string _status = "Connect OBS and Twitch to receive private chat chimes.";
    private string? _lastViewer;
    private DateTimeOffset? _lastPlayedAt;
    private (bool Safe, string Status)? _safetyCache;
    private DateTimeOffset _safetyCacheAt = DateTimeOffset.MinValue;

    public PrivateChatChimeService(ObsWebSocketClient obs, ILogger<PrivateChatChimeService> logger)
    {
        this.obs = obs;
        this.logger = logger;
        Load();
    }

    public async Task<PrivateChatChimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var safety = await GetSafetyAsync(cancellationToken);
        lock (_sync)
        {
            var status = !_enabled
                ? "Private chat chime is off."
                : !obs.IsConnected
                    ? "Connect OBS to play the chime privately."
                    : !safety.Safe
                        ? safety.Status
                        : _status;
            return new PrivateChatChimeSnapshot(
                _enabled,
                _volumeDb,
                obs.IsConnected,
                safety.Safe,
                status,
                _lastViewer,
                _lastPlayedAt);
        }
    }

    public async Task<PrivateChatChimeSnapshot> SetOptionsAsync(
        PrivateChatChimeOptions options,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _enabled = options.Enabled;
            _volumeDb = Math.Round(Math.Clamp(options.VolumeDb, -45, -12), 1);
            _status = _enabled
                ? "Ready for new viewer messages."
                : "Private chat chime is off.";
            SaveUnsafe();
        }
        return await GetSnapshotAsync(cancellationToken);
    }

    public Task<PrivateChatChimeSnapshot> TestAsync(CancellationToken cancellationToken = default) =>
        PlayAsync("Test viewer", true, cancellationToken);

    public Task NotifyAsync(TwitchChatMessage message, CancellationToken cancellationToken = default) =>
        PlayAsync(message.UserName, false, cancellationToken);

    private async Task<PrivateChatChimeSnapshot> PlayAsync(
        string viewer,
        bool force,
        CancellationToken cancellationToken)
    {
        bool enabled;
        double volumeDb;
        lock (_sync)
        {
            enabled = _enabled;
            volumeDb = _volumeDb;
        }
        if (!enabled && !force) return await GetSnapshotAsync(cancellationToken);

        await _playGate.WaitAsync(cancellationToken);
        try
        {
            // Always perform a fresh safety check before making sound. The dashboard can reuse a
            // recent result, but playback must never trust stale OBS routing information.
            var safety = await GetSafetyAsync(cancellationToken, true);
            if (!safety.Safe)
            {
                lock (_sync) _status = safety.Status;
                return await GetSnapshotAsync(cancellationToken);
            }

            var wavePath = EnsureWaveFile();
            var scene = await obs.RequestAsync("GetCurrentProgramScene", null, cancellationToken);
            var sceneName = scene.GetProperty("currentProgramSceneName").GetString()
                ?? throw new InvalidOperationException("OBS did not report the current scene.");
            var inputs = await obs.RequestAsync("GetInputList", null, cancellationToken);
            var exists = inputs.GetProperty("inputs").EnumerateArray().Any(input =>
                string.Equals(input.GetProperty("inputName").GetString(), InputName, StringComparison.Ordinal));

            if (!exists)
            {
                await obs.RequestAsync("CreateInput", new
                {
                    sceneName,
                    inputName = InputName,
                    inputKind = "ffmpeg_source",
                    inputSettings = new { local_file = wavePath, looping = false, restart_on_activate = false },
                    sceneItemEnabled = true
                }, cancellationToken);
            }
            else
            {
                // A global OBS input is attached to each gameplay scene on first use. It contains
                // audio only, so the enabled scene item never covers or changes the video canvas.
                try
                {
                    await obs.RequestAsync("GetSceneItemId", new { sceneName, sourceName = InputName }, cancellationToken);
                }
                catch
                {
                    await obs.RequestAsync("CreateSceneItem", new
                    {
                        sceneName,
                        sourceName = InputName,
                        sceneItemEnabled = true
                    }, cancellationToken);
                }
            }

            await obs.RequestAsync("SetInputAudioMonitorType", new
            {
                inputName = InputName,
                monitorType = "OBS_MONITORING_TYPE_MONITOR_ONLY"
            }, cancellationToken);
            await obs.SetInputVolumeAsync(InputName, volumeDb, cancellationToken);
            await obs.RequestAsync("TriggerMediaInputAction", new
            {
                inputName = InputName,
                mediaAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART"
            }, cancellationToken);

            lock (_sync)
            {
                _lastViewer = viewer;
                _lastPlayedAt = DateTimeOffset.UtcNow;
                _status = $"Private chime played for {viewer}; OBS kept it out of the broadcast mix.";
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not play the private Twitch chat chime.");
            lock (_sync) _status = $"Chat chime could not play: {exception.Message}";
        }
        finally
        {
            _playGate.Release();
        }
        return await GetSnapshotAsync(cancellationToken);
    }

    private async Task<(bool Safe, string Status)> GetSafetyAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        if (!obs.IsConnected) return (false, "Connect OBS to play the chime privately.");
        lock (_sync)
        {
            if (!forceRefresh && _safetyCache is not null
                && DateTimeOffset.UtcNow - _safetyCacheAt < TimeSpan.FromSeconds(20))
                return _safetyCache.Value;
        }
        try
        {
            var inputs = await obs.RequestAsync("GetInputList", null, cancellationToken);
            foreach (var input in inputs.GetProperty("inputs").EnumerateArray())
            {
                var kind = input.GetProperty("inputKind").GetString() ?? "";
                if (!kind.Contains("output_capture", StringComparison.OrdinalIgnoreCase)) continue;
                var name = input.GetProperty("inputName").GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var mute = await obs.RequestAsync("GetInputMute", new { inputName = name }, cancellationToken);
                if (!mute.GetProperty("inputMuted").GetBoolean())
                    return CacheSafety((false, $"Viewer-safe hold: mute OBS “{name}” first. Its desktop/output capture could pick up the monitoring chime."));
            }
            return CacheSafety((true, "Ready. OBS Monitor Only keeps this soft chime out of the stream and VOD mix."));
        }
        catch (Exception exception)
        {
            return CacheSafety((false, $"Could not verify private audio routing: {exception.Message}"));
        }
    }

    private (bool Safe, string Status) CacheSafety((bool Safe, string Status) value)
    {
        lock (_sync)
        {
            _safetyCache = value;
            _safetyCacheAt = DateTimeOffset.UtcNow;
        }
        return value;
    }

    private static string EnsureWaveFile()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamSidekick");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "private-chat-chime.wav");
        if (File.Exists(path)) return path;

        const int sampleRate = 48_000;
        const double durationSeconds = 0.18;
        var sampleCount = (int)(sampleRate * durationSeconds);
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(sampleCount * 2);
        for (var index = 0; index < sampleCount; index++)
        {
            var time = (double)index / sampleRate;
            var progress = (double)index / sampleCount;
            var frequency = progress < .43 ? 660d : 880d;
            var envelope = Math.Sin(Math.PI * progress);
            var value = .32 * envelope * Math.Sin(2 * Math.PI * frequency * time);
            writer.Write((short)(short.MaxValue * value));
        }
        return path;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var options = JsonSerializer.Deserialize<PrivateChatChimeOptions>(File.ReadAllText(_settingsPath));
            if (options is null) return;
            _enabled = options.Enabled;
            _volumeDb = Math.Round(Math.Clamp(options.VolumeDb, -45, -12), 1);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not restore private chat-chime settings.");
        }
    }

    private void SaveUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new PrivateChatChimeOptions(_enabled, _volumeDb)));
            File.Move(temporary, _settingsPath, true);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not save private chat-chime settings.");
        }
    }
}
