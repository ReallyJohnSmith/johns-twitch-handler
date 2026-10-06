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
    private int _samplesSinceCheckpoint;

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

            _active = NewSession(title);
            await SaveInternalAsync(_active, cancellationToken);
            return _active;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts OBS-driven tracking, or resumes a recently checkpointed session after a Handler
    /// restart. The age limit prevents an old unfinished report from being attached to a new show.
    /// </summary>
    public async Task<StreamSession> StartOrResumeRecentAsync(
        string? title,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is not null) return _active;

            var newest = (await ReadSessionsInternalAsync(cancellationToken))
                .Where(session => session.EndedAt is null
                    && DateTimeOffset.UtcNow - session.StartedAt <= TimeSpan.FromHours(18))
                .OrderByDescending(session => session.StartedAt)
                .FirstOrDefault();
            if (newest is not null)
            {
                _active = newest;
                _samplesSinceCheckpoint = 0;
                return newest;
            }

            _active = NewSession(title);
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

    public async Task AddChatMessageAsync(
        TwitchChatMessage message,
        CancellationToken cancellationToken = default)
    {
        if (_active is null) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null) return;
            _active.ChatMessages.Add(new SessionChatMessage
            {
                Id = message.Id,
                At = message.At,
                UserName = message.UserName,
                Text = message.Text
            });
            // Bound unusually busy or very long broadcasts without retaining Twitch account IDs.
            if (_active.ChatMessages.Count > 10_000)
                _active.ChatMessages.RemoveRange(0, 500);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordPerformanceSampleAsync(
        PerformanceSample sample,
        IReadOnlyList<ResourceDrain>? resourceDrains = null,
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

            // Process enumeration runs every 15 seconds, but one archived observation per minute is
            // enough to identify sustained drains later without multiplying report size and disk I/O.
            if (resourceDrains is { Count: > 0 }
                && (_active.ResourceSnapshots.Count == 0
                    || sample.At - _active.ResourceSnapshots[^1].At >= TimeSpan.FromMinutes(1)))
            {
                _active.ResourceSnapshots.Add(new SessionResourceSnapshot(sample.At, resourceDrains));
            }

            // A one-minute checkpoint protects performance and chat data without rewriting a large
            // JSON report every three seconds on the same PC that is running the game and OBS.
            _samplesSinceCheckpoint++;
            if (_samplesSinceCheckpoint >= 20)
            {
                await SaveInternalAsync(_active, cancellationToken);
                _samplesSinceCheckpoint = 0;
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
            _samplesSinceCheckpoint = 0;
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
            return _active is null ? null : BuildReport(_active, isActive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionReport?> LatestReportAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await ReadSessionsInternalAsync(cancellationToken);
        var latest = sessions
            .Where(session => session.EndedAt is not null)
            .OrderByDescending(session => session.StartedAt)
            .FirstOrDefault()
            ?? sessions.OrderByDescending(session => session.StartedAt).FirstOrDefault();
        return latest is null ? null : BuildReport(latest);
    }

    public async Task<IReadOnlyList<SessionHistorySummary>> ListReportsAsync(
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var sessions = await ReadSessionsInternalAsync(cancellationToken);
        return sessions
            .OrderByDescending(session => session.StartedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(session =>
            {
                var report = BuildReport(session);
                return new SessionHistorySummary(
                    report.Id,
                    report.Title,
                    report.StartedAt,
                    report.EndedAt,
                    report.DurationMinutes,
                    report.HighlightCount,
                    report.ChatMessageCount,
                    report.EndedAt is not null,
                    report.PerformanceSummary);
            })
            .ToList();
    }

    public async Task<SessionReport?> ReportAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var session = (await ReadSessionsInternalAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == id);
        return session is null ? null : BuildReport(session);
    }

    public async Task<SessionReport?> SetActiveWardogsBaselineAsync(
        WardogsStatSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null) return null;
            _active.Wardogs = new WardogsSessionData
            {
                Baseline = snapshot,
                Status = $"Baseline captured at {snapshot.CapturedAt.ToLocalTime():t}."
            };
            await SaveInternalAsync(_active, cancellationToken);
            return BuildReport(_active, isActive: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SessionReport?> SetWardogsFinalAsync(
        Guid? sessionId,
        WardogsStatSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StreamSession? session = null;
            var isActive = false;
            if (_active is not null && (sessionId is null || _active.Id == sessionId))
            {
                session = _active;
                isActive = true;
            }
            else
            {
                var sessions = await ReadSessionsInternalAsync(cancellationToken);
                session = sessionId is { } id
                    ? sessions.FirstOrDefault(candidate => candidate.Id == id)
                    : sessions.Where(candidate => candidate.EndedAt is not null)
                        .OrderByDescending(candidate => candidate.StartedAt)
                        .FirstOrDefault();
            }
            if (session is null) return null;

            session.Wardogs ??= new WardogsSessionData();
            session.Wardogs.Final = snapshot;
            session.Wardogs.ReconciledAt = DateTimeOffset.UtcNow;
            var baseline = session.Wardogs.Baseline;
            if (baseline is null)
            {
                session.Wardogs.VerifiedDelta = false;
                session.Wardogs.Status = "Post-stream stats fetched, but this session has no starting baseline.";
            }
            else if (!baseline.SteamId.Equals(snapshot.SteamId, StringComparison.Ordinal)
                || !baseline.Server.Equals(snapshot.Server, StringComparison.OrdinalIgnoreCase))
            {
                session.Wardogs.VerifiedDelta = false;
                session.Wardogs.Status = "The saved baseline belongs to a different Wardogs player or server.";
            }
            else
            {
                var kills = snapshot.Kills - baseline.Kills;
                var deaths = snapshot.Deaths - baseline.Deaths;
                var headshots = snapshot.Headshots - baseline.Headshots;
                var windowIsSafe = snapshot.CapturedAt - baseline.CapturedAt <= TimeSpan.FromHours(23);
                var countsAreSafe = kills >= 0 && deaths >= 0 && headshots >= 0;
                session.Wardogs.VerifiedDelta = windowIsSafe && countsAreSafe;
                if (session.Wardogs.VerifiedDelta)
                {
                    session.Wardogs.Kills = kills;
                    session.Wardogs.Deaths = deaths;
                    session.Wardogs.Headshots = headshots;
                    session.Wardogs.Status = "Calculated from the change in the WARDOGS Tracker 24-hour server leaderboard.";
                }
                else
                {
                    session.Wardogs.Kills = null;
                    session.Wardogs.Deaths = null;
                    session.Wardogs.Headshots = null;
                    session.Wardogs.Status = windowIsSafe
                        ? "The 24-hour leaderboard moved backward, so a reliable session delta could not be calculated."
                        : "More than 23 hours passed after the baseline, so the rolling 24-hour leaderboard cannot provide a reliable session delta.";
                }
            }

            await SaveInternalAsync(session, cancellationToken);
            return BuildReport(session, isActive);
        }
        finally
        {
            _gate.Release();
        }
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

    private async Task<IReadOnlyList<StreamSession>> ReadSessionsInternalAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_dataDirectory)) return [];

        var sessions = new List<StreamSession>();
        foreach (var path in Directory.EnumerateFiles(_dataDirectory, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var session = await JsonSerializer.DeserializeAsync<StreamSession>(stream, JsonOptions, cancellationToken);
                if (session is not null) sessions.Add(session);
            }
            catch (JsonException)
            {
                // A damaged legacy file must not hide the remaining stream history.
            }
            catch (IOException)
            {
                // A checkpoint may be replacing this file; the next history refresh will retry it.
            }
        }
        return sessions;
    }

    private static StreamSession NewSession(string? title) => new()
    {
        Title = string.IsNullOrWhiteSpace(title)
            ? $"Stream {DateTimeOffset.Now:MMM d, yyyy h:mm tt}"
            : title.Trim()
    };

    private static SessionReport BuildReport(StreamSession session, bool isActive = false)
    {
        // An old interrupted file should stop at its last recorded observation, not grow in duration
        // forever. Only the in-memory current session uses the present time.
        var end = session.EndedAt ?? (isActive ? DateTimeOffset.UtcNow : LastObservedAt(session));
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
            session.ChatMessages.Count,
            Math.Round(averageCpu, 1),
            Math.Round(peakCpu, 1),
            Math.Round(averageGpu, 1),
            Math.Round(peakGpu, 1),
            Math.Round(peakVram, 1),
            DescribePerformance(averageCpu, peakCpu, averageGpu, peakGpu, peakVram),
            session.Markers.OrderBy(marker => marker.OffsetSeconds).ToList(),
            session.Activities.OrderBy(activity => activity.At).ToList(),
            session.ChatMessages.OrderBy(message => message.At).ToList(),
            BuildResourceSummary(session),
            session.Wardogs,
            BuildTimeline(session));
    }

    private static DateTimeOffset LastObservedAt(StreamSession session)
    {
        var latest = session.StartedAt;
        if (session.Markers.Count > 0) latest = Max(latest, session.Markers.Max(marker => marker.CreatedAt));
        if (session.Activities.Count > 0) latest = Max(latest, session.Activities.Max(activity => activity.At));
        if (session.ChatMessages.Count > 0) latest = Max(latest, session.ChatMessages.Max(message => message.At));
        if (session.PerformanceSamples.Count > 0) latest = Max(latest, session.PerformanceSamples.Max(sample => sample.At));
        if (session.ResourceSnapshots.Count > 0) latest = Max(latest, session.ResourceSnapshots.Max(sample => sample.At));
        return latest;

        static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
    }

    private static IReadOnlyList<SessionResourceSummary> BuildResourceSummary(StreamSession session) =>
        session.ResourceSnapshots
            .SelectMany(snapshot => snapshot.Drains)
            .GroupBy(drain => drain.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SessionResourceSummary(
                group.Key,
                group.Count(),
                Math.Round(group.Average(drain => drain.CpuPercent), 1),
                Math.Round(group.Max(drain => drain.CpuPercent), 1),
                Math.Round(group.Average(drain => drain.MemoryMb)),
                Math.Round(group.Max(drain => drain.MemoryMb)),
                group.Last().Assessment,
                group.Last().Recommendation))
            .OrderByDescending(item => item.Observations)
            .ThenByDescending(item => item.AverageCpuPercent)
            .Take(5)
            .ToList();

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
