using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Serializes changes to the active session and persists a human-readable JSON report locally.
/// The semaphore prevents markers, scene events, and performance samples from mutating lists together.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataDirectory;
    private StreamSession? _active;

    public SessionStore(IConfiguration configuration)
    {
        _dataDirectory = configuration["DataDirectory"]
            ?? Environment.GetEnvironmentVariable("STREAMSIDEKICK_DATA")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamSidekick",
                "sessions");
    }

    public bool HasActiveSession => Volatile.Read(ref _active) is not null;

    public async Task<StreamSession> StartAsync(string? title, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is not null)
            {
                return _active;
            }

            _active = new StreamSession
            {
                Title = string.IsNullOrWhiteSpace(title)
                    ? $"Stream {DateTimeOffset.Now:MMM d, yyyy h:mm tt}"
                    : title.Trim()
            };
            await SaveInternalAsync(_active, cancellationToken);
            return _active;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StreamMarker> AddMarkerAsync(
        string? title,
        string? kind,
        bool replaySaveRequested,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _active ??= new StreamSession
            {
                Title = $"Stream {DateTimeOffset.Now:MMM d, yyyy h:mm tt}"
            };

            var now = DateTimeOffset.UtcNow;
            var marker = new StreamMarker
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Highlight" : title.Trim(),
                Kind = string.IsNullOrWhiteSpace(kind) ? "clip" : kind.Trim().ToLowerInvariant(),
                CreatedAt = now,
                OffsetSeconds = Math.Max(0, (now - _active.StartedAt).TotalSeconds),
                ReplaySaveRequested = replaySaveRequested,
                Note = note
            };
            _active.Markers.Add(marker);
            await SaveInternalAsync(_active, cancellationToken);
            return marker;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateMarkerClipAsync(
        Guid markerId,
        TwitchClipResult clip,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var marker = _active?.Markers.FirstOrDefault(candidate => candidate.Id == markerId);
            if (marker is null || _active is null)
            {
                return;
            }

            marker.TwitchClipId = clip.Id;
            marker.TwitchEditUrl = clip.EditUrl;
            await SaveInternalAsync(_active, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddActivityAsync(
        string type,
        string detail,
        CancellationToken cancellationToken = default)
    {
        if (_active is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null)
            {
                return;
            }

            _active.Activities.Add(new SessionActivity { Type = type, Detail = detail });
            await SaveInternalAsync(_active, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordPerformanceSampleAsync(
        PerformanceSample sample,
        CancellationToken cancellationToken = default)
    {
        if (_active is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null)
            {
                return;
            }

            _active.PerformanceSamples.Add(sample);
            // At one sample every three seconds this retains roughly 12 hours, then trims in batches.
            if (_active.PerformanceSamples.Count > 14_400)
            {
                _active.PerformanceSamples.RemoveRange(0, 1_000);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionReport?> EndAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null)
            {
                return null;
            }

            _active.EndedAt = DateTimeOffset.UtcNow;
            await SaveInternalAsync(_active, cancellationToken);
            var report = BuildReport(_active);
            _active = null;
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionReport?> CurrentReportAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _active is null ? null : BuildReport(_active);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionReport?> LatestReportAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_dataDirectory))
        {
            return null;
        }

        var latestPath = Directory.EnumerateFiles(_dataDirectory, "*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;

        if (latestPath is null)
        {
            return null;
        }

        await using var stream = File.OpenRead(latestPath);
        var session = await JsonSerializer.DeserializeAsync<StreamSession>(stream, JsonOptions, cancellationToken);
        return session is null ? null : BuildReport(session);
    }

    private async Task SaveInternalAsync(StreamSession session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, $"{session.StartedAt:yyyyMMdd-HHmmss}-{session.Id:N}.json");
        var temporary = path + ".tmp";
        try
        {
            // Serialize completely before replacement so an interrupted write cannot truncate the last good report.
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static SessionReport BuildReport(StreamSession session)
    {
        var end = session.EndedAt ?? DateTimeOffset.UtcNow;
        var samples = session.PerformanceSamples;
        var averageCpu = samples.Count == 0 ? 0 : samples.Average(sample => sample.CpuPercent);
        var peakCpu = samples.Count == 0 ? 0 : samples.Max(sample => sample.CpuPercent);
        var averageGpu = samples.Count == 0 ? 0 : samples.Average(sample => sample.GpuPercent);
        var peakGpu = samples.Count == 0 ? 0 : samples.Max(sample => sample.GpuPercent);
        var peakVram = samples.Count == 0 ? 0 : samples.Max(sample => sample.VramPercent);
        return new SessionReport(
            session.Id,
            session.Title,
            session.StartedAt,
            session.EndedAt,
            Math.Round(Math.Max(0, (end - session.StartedAt).TotalMinutes), 1),
            session.Markers.Count,
            session.Activities.Count(activity => activity.Type == "scene"),
            Math.Round(averageCpu, 1),
            Math.Round(peakCpu, 1),
            Math.Round(averageGpu, 1),
            Math.Round(peakGpu, 1),
            Math.Round(peakVram, 1),
            DescribePerformance(averageCpu, peakCpu, averageGpu, peakGpu, peakVram),
            session.Markers.OrderBy(marker => marker.OffsetSeconds).ToList(),
            session.Activities.OrderBy(activity => activity.At).ToList(),
            BuildTimeline(session));
    }

    private static IReadOnlyList<SessionTimelineItem> BuildTimeline(StreamSession session)
    {
        var timeline = new List<SessionTimelineItem>();
        timeline.AddRange(session.Markers.Select(marker => new SessionTimelineItem(
            marker.CreatedAt,
            marker.OffsetSeconds,
            "marker",
            marker.Title,
            marker.Note ?? marker.Kind,
            "highlight")));
        timeline.AddRange(session.Activities.Select(activity => new SessionTimelineItem(
            activity.At,
            Math.Max(0, (activity.At - session.StartedAt).TotalSeconds),
            activity.Type,
            ActivityTitle(activity.Type),
            activity.Detail,
            activity.Type is "warning" or "internet" ? "warning" : "info")));

        // Convert long sample streams into threshold crossings so a ten-minute CPU problem becomes
        // one useful timeline event rather than hundreds of nearly identical points.
        var cpuHot = false;
        var gpuHot = false;
        var memoryHot = false;
        foreach (var sample in session.PerformanceSamples.OrderBy(sample => sample.At))
        {
            AddThreshold("CPU pressure", "performance", sample.CpuPercent, ref cpuHot);
            AddThreshold("GPU pressure", "performance", sample.GpuPercent, ref gpuHot);
            AddThreshold("Memory pressure", "performance", Math.Max(sample.MemoryPercent, sample.VramPercent), ref memoryHot);

            void AddThreshold(string title, string type, double value, ref bool active)
            {
                if (!active && value >= 90)
                {
                    timeline.Add(new SessionTimelineItem(
                        sample.At,
                        Math.Max(0, (sample.At - session.StartedAt).TotalSeconds),
                        type,
                        title,
                        $"Reached {value:0}%",
                        value >= 97 ? "critical" : "warning"));
                    active = true;
                }
                else if (active && value < 85)
                {
                    active = false;
                }
            }
        }

        return timeline.OrderBy(item => item.At).ToList();
    }

    private static string ActivityTitle(string type) => type switch
    {
        "scene" => "Scene changed",
        "follow" => "New follower",
        "subscription" or "resub" => "Subscription",
        "gift" => "Gift subscriptions",
        "cheer" => "Bits",
        "raid" => "Incoming raid",
        "reward" => "Channel Points",
        "ad" => "Ad break",
        "internet" => "Connection issue",
        "tangia" => "Tangia",
        "live" => "Twitch went live",
        "offline" => "Twitch stream ended",
        "wow" => "WoW encounter",
        _ => "Stream activity"
    };

    private static string DescribePerformance(
        double averageCpu,
        double peakCpu,
        double averageGpu,
        double peakGpu,
        double peakVram)
    {
        if (averageGpu >= 90 || peakGpu >= 98)
        {
            return "GPU pressure was the likely limiter. Cap the game's frame rate or lower GPU-heavy game settings to preserve OBS rendering headroom.";
        }

        if (averageCpu >= 90 || peakCpu >= 98)
        {
            return "CPU pressure was the likely limiter. Keep NVENC enabled and reduce CPU-heavy game, browser-source, or background-app load.";
        }

        if (peakVram >= 94)
        {
            return "VRAM pressure was high. Lower texture quality or close GPU-accelerated background apps.";
        }

        if (samplesAreMissing(averageCpu, peakCpu, averageGpu, peakGpu))
        {
            return "No performance samples were collected for this session.";
        }

        return "CPU and GPU headroom looked healthy for most of the session.";

        static bool samplesAreMissing(double avgCpu, double maxCpu, double avgGpu, double maxGpu) =>
            avgCpu == 0 && maxCpu == 0 && avgGpu == 0 && maxGpu == 0;
    }
}
