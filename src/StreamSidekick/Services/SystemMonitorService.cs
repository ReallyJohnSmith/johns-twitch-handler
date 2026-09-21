using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace StreamSidekick.Services;

/// <summary>
/// Samples total CPU/RAM plus NVIDIA GPU telemetry in the background. Process-level CPU is derived
/// from changes in cumulative processor time, which avoids heavier Windows performance-counter queries.
/// </summary>
public sealed class SystemMonitorService(SessionStore sessions, ILogger<SystemMonitorService> logger)
    : BackgroundService
{
    private sealed record ProcessReading(string Name, TimeSpan CpuTime, long WorkingSetBytes, DateTimeOffset At);

    private readonly object _sync = new();
    private readonly Queue<PerformanceSample> _history = new();
    private Dictionary<int, ProcessReading> _previousProcesses = [];
    private IReadOnlyList<ResourceDrain> _resourceDrains = [];
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private DateTimeOffset _nextResourceDrainSampleAt = DateTimeOffset.MinValue;
    private PerformanceSample _current = new(DateTimeOffset.UtcNow, 0, 0, 0, 0, null);

    public SystemPerformanceSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var warnings = new List<string>();
            if (_current.GpuPercent >= 95) warnings.Add("GPU is saturated; OBS may lose rendering time.");
            if (_current.CpuPercent >= 95) warnings.Add("CPU is saturated; game or background tasks may stutter.");
            if (_current.VramPercent >= 92) warnings.Add("VRAM is nearly full.");
            if (_current.MemoryPercent >= 92) warnings.Add("System memory is nearly full.");
            if (_current.GpuTemperatureC >= 83) warnings.Add("GPU temperature is high.");
            return new SystemPerformanceSnapshot(_current, _history.ToList(), warnings, _resourceDrains.ToList());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var capture = await CaptureAsync(stoppingToken);
                lock (_sync)
                {
                    _current = capture.Sample;
                    // Process inventory is intentionally less frequent than gauges; retain the last
                    // ranking between scans instead of discarding it on lightweight sample ticks.
                    if (capture.ResourceDrains is not null) _resourceDrains = capture.ResourceDrains;
                    _history.Enqueue(capture.Sample);
                    while (_history.Count > 120)
                    {
                        _history.Dequeue();
                    }
                }
                await sessions.RecordPerformanceSampleAsync(capture.Sample, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Performance sample failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task<(PerformanceSample Sample, IReadOnlyList<ResourceDrain>? ResourceDrains)> CaptureAsync(
        CancellationToken cancellationToken)
    {
        var cpu = GetCpuUsage();
        var memory = GetMemoryUsage();
        var gpu = await GetNvidiaStatsAsync(cancellationToken);
        var sample = new PerformanceSample(
            DateTimeOffset.UtcNow,
            Math.Round(cpu, 1),
            Math.Round(gpu.Usage, 1),
            Math.Round(memory.Percent, 1),
            Math.Round(gpu.VramPercent, 1),
            gpu.Temperature,
            gpu.Name);
        IReadOnlyList<ResourceDrain>? resourceDrains = null;
        var now = DateTimeOffset.UtcNow;
        if (now >= _nextResourceDrainSampleAt)
        {
            // Process rankings are diagnostic rather than frame-by-frame telemetry. Fifteen seconds
            // still catches sustained drains while leaving more CPU time for the game and OBS.
            resourceDrains = GetResourceDrains(memory.TotalPhysicalBytes);
            _nextResourceDrainSampleAt = now.AddSeconds(15);
        }
        return (sample, resourceDrains);
    }

    private double GetCpuUsage()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        var idleDelta = idleValue - _previousIdle;
        var kernelDelta = kernelValue - _previousKernel;
        var userDelta = userValue - _previousUser;
        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        var total = kernelDelta + userDelta;
        return total == 0 ? 0 : Math.Clamp(100d * (total - idleDelta) / total, 0, 100);
    }

    private static (double Percent, ulong TotalPhysicalBytes) GetMemoryUsage()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status) ? (status.MemoryLoad, status.TotalPhysical) : (0, 0);
    }

    private IReadOnlyList<ResourceDrain> GetResourceDrains(ulong totalPhysicalBytes)
    {
        var now = DateTimeOffset.UtcNow;
        var current = new Dictionary<int, ProcessReading>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var reading = new ProcessReading(process.ProcessName, process.TotalProcessorTime, process.WorkingSet64, now);
                current[process.Id] = reading;
            }
            catch
            {
                // Protected and short-lived processes are expected to disappear during enumeration.
            }
            finally
            {
                process.Dispose();
            }
        }

        var grouped = current.Select(item =>
        {
            var cpuPercent = 0d;
            if (_previousProcesses.TryGetValue(item.Key, out var previous)
                && previous.Name.Equals(item.Value.Name, StringComparison.OrdinalIgnoreCase))
            {
                var elapsedMs = (item.Value.At - previous.At).TotalMilliseconds;
                var cpuMs = (item.Value.CpuTime - previous.CpuTime).TotalMilliseconds;
                if (elapsedMs > 0 && cpuMs >= 0)
                    cpuPercent = Math.Clamp(cpuMs / elapsedMs / Environment.ProcessorCount * 100, 0, 100);
            }
            return new { Name = FriendlyProcessName(item.Value.Name), item.Value.WorkingSetBytes, CpuPercent = cpuPercent };
        }).GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
          .Select(group => new
          {
              Name = group.Key,
              ProcessCount = group.Count(),
              CpuPercent = group.Sum(item => item.CpuPercent),
              MemoryBytes = group.Sum(item => item.WorkingSetBytes)
          })
          .Select(group => new
          {
              group.Name,
              group.ProcessCount,
              CpuPercent = Math.Clamp(group.CpuPercent, 0, 100),
              group.MemoryBytes,
              // CPU gets a slight priority, while RAM share ensures idle memory-heavy apps still surface.
              Score = group.CpuPercent * 1.25 + (totalPhysicalBytes == 0 ? 0 : group.MemoryBytes * 100d / totalPhysicalBytes)
          })
          .OrderByDescending(group => group.Score)
          .ThenByDescending(group => group.MemoryBytes)
          .Take(5)
          .Select(group =>
          {
              var guidance = DescribeProcess(group.Name);
              return new ResourceDrain(
                  group.Name,
                  group.ProcessCount,
                  Math.Round(group.CpuPercent, 1),
                  Math.Round(group.MemoryBytes / 1024d / 1024d),
                  guidance.Assessment,
                  guidance.Recommendation);
          })
          .ToList();

        _previousProcesses = current;
        return grouped;
    }

    private static string FriendlyProcessName(string processName) => processName.ToLowerInvariant() switch
    {
        "wow" or "wowclassic" => "World of Warcraft",
        "obs64" or "obs32" => "OBS Studio",
        "nvidia broadcast" or "nvidiabroadcast" or "nvbroadcast" => "NVIDIA Broadcast",
        "msedge" => "Microsoft Edge",
        "msedgewebview2" => "Microsoft WebView apps",
        "chrome" => "Google Chrome",
        "firefox" => "Mozilla Firefox",
        "discord" => "Discord",
        "spotify" => "Spotify",
        "chatgpt" => "Desktop AI assistant",
        "dropbox" => "Dropbox",
        "steam" or "steamwebhelper" => "Steam",
        "msmpeng" => "Windows Defender",
        "dwm" => "Desktop Window Manager",
        "streamsidekick" => "John's Twitch Handler",
        "john's twitch handler" => "John's Twitch Handler",
        "johnstwitchhandler" => "John's Twitch Handler",
        _ => processName
    };

    private static (string Assessment, string Recommendation) DescribeProcess(string name) => name switch
    {
        "World of Warcraft" => ("Expected while gaming · Usually fixable", "Cap foreground FPS first; then lower shadows, render scale, or heavy addons."),
        "OBS Studio" => ("Expected while streaming · Tunable", "Keep NVENC and the 720p30 preset; simplify browser sources if CPU remains high."),
        "NVIDIA Broadcast" => ("Common with effects · Tunable", "Disable camera/background effects you do not use; microphone cleanup is usually lighter."),
        "Google Chrome" or "Microsoft Edge" or "Mozilla Firefox" => ("Common background drain · Fixable", "Close unused tabs and video pages; keep only required stream dashboards open."),
        "Microsoft WebView apps" => ("Common shared app runtime · Fixable", "Close unused widgets and desktop apps that embed web pages before streaming."),
        "Discord" => ("Common while streaming · Fixable", "Disable the game overlay and close active video or screen-share previews."),
        "Spotify" => ("Normal for song requests · Optional", "Keep it open for requests; restart it if memory grows unusually high."),
        "Desktop AI assistant" => ("Common while open · Optional", "Close unused assistant windows before going live; desktop apps can use several helper processes."),
        "Dropbox" => ("Common background sync · Fixable", "Pause syncing while live, especially when it is uploading files."),
        "Steam" => ("Common with games · Partly fixable", "Keep Steam running for WoW only if needed; close Store, chat, and animated library windows."),
        "Windows Defender" => ("Common during scans · Temporary", "Let the scan finish or schedule scans outside streams; do not disable protection."),
        "Desktop Window Manager" => ("Windows component · Indirectly tunable", "Do not close it; reduce animated windows, overlays, or extra high-refresh displays."),
        "John's Twitch Handler" => ("Expected at low levels · Fixable if high", "Refresh/restart John's Twitch Handler if sustained CPU is high; report repeat spikes."),
        _ => ("May be normal · Check whether needed", "If you recognize it and do not need it while live, close it before streaming.")
    };

    private static async Task<(string? Name, double Usage, double VramPercent, double? Temperature)> GetNvidiaStatsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi.exe",
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return (null, 0, 0, null);
            var output = await process.StandardOutput.ReadLineAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var parts = output?.Split(',', StringSplitOptions.TrimEntries);
            if (parts is null || parts.Length < 5) return (null, 0, 0, null);

            var usage = Parse(parts[1]);
            var used = Parse(parts[2]);
            var total = Parse(parts[3]);
            var temperature = Parse(parts[4]);
            return (parts[0], usage, total <= 0 ? 0 : used / total * 100, temperature);
        }
        catch
        {
            return (null, 0, 0, null);
        }

        static double Parse(string value) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
