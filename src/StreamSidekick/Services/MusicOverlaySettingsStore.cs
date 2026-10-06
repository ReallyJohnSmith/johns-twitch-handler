using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamSidekick.Services;

public sealed record MusicOverlaySettings([property: JsonRequired] bool Visible);

/// <summary>Only controls the viewer-facing graphics, never Spotify playback or requests.</summary>
public sealed class MusicOverlaySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string _path;
    private MusicOverlaySettings _settings = new(true);

    public MusicOverlaySettingsStore(IConfiguration configuration, ILogger<MusicOverlaySettingsStore> logger)
    {
        var directory = configuration["PreferencesDirectory"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSidekick");
        _path = Path.Combine(directory, "music-overlay.json");
        try
        {
            if (File.Exists(_path))
                _settings = JsonSerializer.Deserialize<MusicOverlaySettings>(File.ReadAllText(_path), JsonOptions)
                    ?? throw new JsonException("The overlay settings were empty.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // If a saved preference cannot be read, do not unexpectedly put graphics back on stream.
            _settings = new(false);
            logger.LogWarning(exception, "Music overlay settings could not be read. The overlay will stay hidden until it is shown again.");
        }
    }

    public MusicOverlaySettings Get()
    {
        lock (_sync) return _settings;
    }

    public MusicOverlaySettings Set(MusicOverlaySettings settings)
    {
        lock (_sync)
        {
            if (_settings == settings) return _settings;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, true);
            // Publish only after saving succeeds. A failed click must not pretend it was remembered.
            _settings = settings;
            return _settings;
        }
    }
}
