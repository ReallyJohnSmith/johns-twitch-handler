using System.Collections.Concurrent;

namespace StreamSidekick.Services;

public sealed record WowEventMonitorOptions(
    bool AutoMarkBossKills,
    bool AutoSaveReplay,
    bool AutoTangiaBossFocus = true);
public sealed record WowGameEvent(string Id, DateTimeOffset At, string EditionId, string Type, string Title, bool? Success);
public sealed record WowEventMonitorSnapshot(
    bool Watching,
    bool CombatLogFound,
    bool AutoMarkBossKills,
    bool AutoSaveReplay,
    bool AutoTangiaBossFocus,
    int BossFocusTestsRemaining,
    string BossFocusTestStatus,
    string Status,
    IReadOnlyList<WowGameEvent> RecentEvents);
public sealed record BossFocusVerificationFeedback(
    int TestNumber,
    bool Passed,
    bool Completed,
    string Message);

/// <summary>
/// Tails WoW combat logs without locking them, publishes encounter boundaries, and remembers byte
/// positions so each one-second scan reads only newly appended text rather than the entire log.
/// </summary>
public sealed class WowEventMonitorService(WowAddonService addons, ILogger<WowEventMonitorService> logger) : BackgroundService
{
    private sealed record TrackedCombatLog(string EditionId, string Path);
    private const int BossFocusTestTarget = 3;
    private readonly ConcurrentDictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly Queue<WowGameEvent> _events = new();
    private bool _combatLogFound;
    private bool _autoMarkBossKills = true;
    private bool _autoSaveReplay = true;
    private bool _autoTangiaBossFocus = true;
    private readonly string _verificationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "boss-focus-verification.txt");
    private int _bossFocusTestsCompleted;
    private int? _bossFocusTestInProgress;
    private string _bossFocusTestStatus = "The first three real boss encounters will verify Tangia Boss Focus.";
    private IReadOnlyList<TrackedCombatLog> _trackedLogs = [];
    private DateTimeOffset _nextLogDiscoveryAt = DateTimeOffset.MinValue;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_verificationPath)
                && int.TryParse(File.ReadAllText(_verificationPath).Trim(), out var completed))
                _bossFocusTestsCompleted = Math.Clamp(completed, 0, BossFocusTestTarget);
            if (_bossFocusTestsCompleted >= BossFocusTestTarget)
                _bossFocusTestStatus = "Boss Focus verification completed successfully for the first three encounters.";
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not restore the Boss Focus verification count.");
        }
        return base.StartAsync(cancellationToken);
    }

    public event Action<WowGameEvent>? EventDetected;
    public bool AutoMarkBossKills { get { lock (_sync) return _autoMarkBossKills; } }
    public bool AutoSaveReplay { get { lock (_sync) return _autoSaveReplay; } }
    public bool AutoTangiaBossFocus { get { lock (_sync) return _autoTangiaBossFocus; } }

    public WowEventMonitorSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var status = _combatLogFound
                ? "Watching the live WoW combat log for boss encounters."
                : "No combat log found. In WoW, type /combatlog once after entering the world.";
            return new WowEventMonitorSnapshot(
                true,
                _combatLogFound,
                _autoMarkBossKills,
                _autoSaveReplay,
                _autoTangiaBossFocus,
                Math.Max(0, BossFocusTestTarget - _bossFocusTestsCompleted),
                _bossFocusTestStatus,
                status,
                _events.Reverse().ToList());
        }
    }

    public WowEventMonitorSnapshot SetOptions(WowEventMonitorOptions options)
    {
        lock (_sync)
        {
            _autoMarkBossKills = options.AutoMarkBossKills;
            _autoSaveReplay = options.AutoSaveReplay;
            _autoTangiaBossFocus = options.AutoTangiaBossFocus;
        }
        return GetSnapshot();
    }

    public WowEventMonitorSnapshot AddTestEvent()
    {
        Publish(new WowGameEvent(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "test", "encounter-end", "Training Dummy defeated", true));
        return GetSnapshot();
    }

    /// <summary>
    /// Records verification feedback for real encounters only. A successful start remains pending
    /// until the matching encounter end confirms that Boss Focus released cleanly.
    /// </summary>
    public BossFocusVerificationFeedback? RecordBossFocusVerification(
        string phase,
        bool passed,
        string detail)
    {
        lock (_sync)
        {
            if (_bossFocusTestsCompleted >= BossFocusTestTarget) return null;
            var starting = phase.Equals("start", StringComparison.OrdinalIgnoreCase);
            if (starting)
            {
                var testNumber = _bossFocusTestsCompleted + 1;
                if (passed)
                {
                    _bossFocusTestInProgress = testNumber;
                    _bossFocusTestStatus = $"Boss Focus check {testNumber}/{BossFocusTestTarget}: Tangia muted; waiting for the encounter to end.";
                    logger.LogInformation("{Status} {Detail}", _bossFocusTestStatus, detail);
                    return new BossFocusVerificationFeedback(testNumber, true, false, _bossFocusTestStatus);
                }

                _bossFocusTestInProgress = null;
                CompleteVerificationUnsafe();
                _bossFocusTestStatus = $"Boss Focus check {testNumber}/{BossFocusTestTarget} failed at pull start: {detail}";
                logger.LogWarning("{Status}", _bossFocusTestStatus);
                return new BossFocusVerificationFeedback(testNumber, false, true, _bossFocusTestStatus);
            }

            if (_bossFocusTestInProgress is not { } activeTest) return null;
            _bossFocusTestInProgress = null;
            CompleteVerificationUnsafe();
            _bossFocusTestStatus = passed
                ? $"Boss Focus check {activeTest}/{BossFocusTestTarget} passed: Tangia muted for the pull and its prior audio state was restored."
                : $"Boss Focus check {activeTest}/{BossFocusTestTarget} failed when the encounter ended: {detail}";
            logger.Log(passed ? LogLevel.Information : LogLevel.Warning, "{Status}", _bossFocusTestStatus);
            return new BossFocusVerificationFeedback(activeTest, passed, true, _bossFocusTestStatus);
        }
    }

    private void CompleteVerificationUnsafe()
    {
        _bossFocusTestsCompleted = Math.Min(BossFocusTestTarget, _bossFocusTestsCompleted + 1);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_verificationPath)!);
            var temporary = _verificationPath + ".tmp";
            File.WriteAllText(temporary, _bossFocusTestsCompleted.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Move(temporary, _verificationPath, true);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not persist the Boss Focus verification count.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ScanLogs();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "WoW event log scan failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void ScanLogs()
    {
        var now = DateTimeOffset.UtcNow;
        if (_trackedLogs.Count == 0 || now >= _nextLogDiscoveryAt)
        {
            _trackedLogs = DiscoverLatestLogs();
            // Folder enumeration is the expensive part. Once a log is known, checking its length is
            // cheap enough to keep encounter detection at one-second resolution.
            _nextLogDiscoveryAt = now.AddSeconds(_trackedLogs.Count == 0 ? 30 : 15);
            var activePaths = _trackedLogs.Select(log => log.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _positions.Keys.Where(path => !activePaths.Contains(path)))
                _positions.TryRemove(path, out _);
        }

        var found = false;
        foreach (var tracked in _trackedLogs)
        {
            var latest = new FileInfo(tracked.Path);
            latest.Refresh();
            if (!latest.Exists)
            {
                _nextLogDiscoveryAt = DateTimeOffset.MinValue;
                continue;
            }
            found = true;

            if (!_positions.TryGetValue(tracked.Path, out var position))
            {
                _positions[tracked.Path] = latest.Length;
                continue;
            }
            if (latest.Length < position) position = 0;
            if (latest.Length == position) continue;

            using var stream = new FileStream(tracked.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var gameEvent = ParseLine(tracked.EditionId, line);
                if (gameEvent is not null) Publish(gameEvent);
            }
            _positions[tracked.Path] = stream.Position;
        }
        lock (_sync) _combatLogFound = found;
    }

    private IReadOnlyList<TrackedCombatLog> DiscoverLatestLogs()
    {
        var result = new List<TrackedCombatLog>();
        foreach (var edition in addons.GetEditionLocations())
        {
            var logs = Path.Combine(edition.Path, "Logs");
            if (!Directory.Exists(logs)) continue;
            FileInfo? latest;
            try
            {
                latest = Directory.EnumerateFiles(logs, "WoWCombatLog*.txt", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch { continue; }
            if (latest is null) continue;
            result.Add(new TrackedCombatLog(edition.Id, latest.FullName));
        }
        return result;
    }

    private static WowGameEvent? ParseLine(string editionId, string line)
    {
        var start = line.IndexOf("ENCOUNTER_START,", StringComparison.Ordinal);
        var end = line.IndexOf("ENCOUNTER_END,", StringComparison.Ordinal);
        if (start < 0 && end < 0) return null;
        var isEnd = end >= 0;
        var payload = line[(isEnd ? end : start)..];
        var fields = ParseCsv(payload);
        if (fields.Count < 3) return null;
        var title = fields[2].Trim('"');
        bool? success = null;
        if (isEnd && fields.Count >= 6) success = fields[5].Trim() == "1";
        return new WowGameEvent(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            editionId,
            isEnd ? "encounter-end" : "encounter-start",
            string.IsNullOrWhiteSpace(title) ? "WoW encounter" : title,
            success);
    }

    private static List<string> ParseCsv(string value)
    {
        // Combat-log encounter names may contain commas, so a plain string split would corrupt fields.
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in value)
        {
            if (character == '"') quoted = !quoted;
            if (character == ',' && !quoted)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else current.Append(character);
        }
        result.Add(current.ToString());
        return result;
    }

    private void Publish(WowGameEvent gameEvent)
    {
        lock (_sync)
        {
            _events.Enqueue(gameEvent);
            while (_events.Count > 12) _events.Dequeue();
        }
        EventDetected?.Invoke(gameEvent);
    }
}
