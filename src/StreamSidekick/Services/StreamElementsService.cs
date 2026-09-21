using System.Diagnostics;

namespace StreamSidekick.Services;

public sealed record StreamElementsStatus(
    bool Installed,
    bool ObsRunning,
    string Status,
    string? PluginPath,
    string DashboardUrl,
    string SetupGuideUrl,
    string MultistreamGuideUrl);

/// <summary>
/// Detects the SE.Live OBS plugin without reading account tokens, stream keys, or private overlay URLs.
/// StreamElements owns authentication and output control inside OBS; the Handler only reports readiness.
/// </summary>
public sealed class StreamElementsService(IConfiguration configuration)
{
    // Plugin installation changes rarely. Caching keeps preflight refreshes from repeatedly walking
    // OBS plugin folders while a game and stream are competing for disk and CPU time.
    private static readonly TimeSpan DetectionLifetime = TimeSpan.FromMinutes(5);
    private readonly object _cacheLock = new();
    private readonly string? _configuredPath = configuration["StreamElements:PluginPath"];
    private string? _cachedPluginPath;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public StreamElementsStatus GetStatus()
    {
        var pluginPath = FindPluginPath();
        var obsRunning = ProcessUtilities.IsAnyRunning(["obs64", "obs32", "obs"]);
        var status = pluginPath is null
            ? "SE.Live was not detected. Install it to connect StreamElements and configure multiple outputs."
            : !obsRunning
                ? "SE.Live is installed. Open OBS to sign in and check multistream outputs."
                : "SE.Live is installed and OBS is running. Confirm each enabled output shows Live in the SE.Live dock.";
        return new StreamElementsStatus(
            pluginPath is not null,
            obsRunning,
            status,
            pluginPath,
            "https://streamelements.com/dashboard",
            "https://docs.streamelements.com/selive/getting-started",
            "https://docs.streamelements.com/selive/guides/multistreaming");
    }

    private string? FindPluginPath()
    {
        lock (_cacheLock)
        {
            if (DateTimeOffset.UtcNow - _cachedAt < DetectionLifetime) return _cachedPluginPath;
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "obs-plugins", "64bit"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obs-studio", "plugins"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugins")
        };
        var explicitCandidates = new[]
        {
            _configuredPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "obs-plugins", "64bit", "obs-streamelements.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obs-studio", "plugins", "obs-streamelements", "bin", "64bit", "obs-streamelements.dll")
        };
        var found = explicitCandidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

        if (found is null)
        {
            foreach (var root in roots.Where(Directory.Exists))
            {
                try
                {
                    found = Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
                        .FirstOrDefault(path =>
                            Path.GetFileName(path).Contains("streamelements", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(path).Contains("selive", StringComparison.OrdinalIgnoreCase));
                    if (found is not null) break;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    // A protected plugin folder should make detection inconclusive, not break preflight.
                }
            }
        }

        lock (_cacheLock)
        {
            _cachedPluginPath = found;
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedPluginPath;
        }
    }
}
