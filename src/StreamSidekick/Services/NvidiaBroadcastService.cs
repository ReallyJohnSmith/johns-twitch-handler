using System.Diagnostics;

namespace StreamSidekick.Services;

public sealed record NvidiaBroadcastStatus(bool Installed, bool Running, string? ExecutablePath);

/// <summary>Finds and launches NVIDIA Broadcast without requiring a system-wide installer lookup.</summary>
public sealed class NvidiaBroadcastService(IConfiguration configuration)
{
    private static readonly string[] ProcessNames =
    [
        "NVIDIA Broadcast",
        "NVIDIABroadcast",
        "NvBroadcast"
    ];

    private readonly string? _configuredPath = configuration["NvidiaBroadcast:ExecutablePath"];

    public NvidiaBroadcastStatus GetStatus()
    {
        var executablePath = FindExecutable();
        return new NvidiaBroadcastStatus(
            executablePath is not null,
            ProcessUtilities.IsAnyRunning(ProcessNames),
            executablePath);
    }

    public NvidiaBroadcastStatus Launch()
    {
        var status = GetStatus();
        if (!status.Installed || status.ExecutablePath is null)
        {
            throw new FileNotFoundException(
                "NVIDIA Broadcast was not found. Set NvidiaBroadcast:ExecutablePath in appsettings.json if it is installed elsewhere.");
        }

        if (!status.Running)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = status.ExecutablePath,
                UseShellExecute = true
            });
        }

        return GetStatus();
    }

    private string? FindExecutable()
    {
        // The explicit setting supports custom installs; the standard path keeps setup zero-config for most users.
        var candidates = new[]
        {
            _configuredPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVIDIA Broadcast",
                "NVIDIA Broadcast.exe")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }
}
