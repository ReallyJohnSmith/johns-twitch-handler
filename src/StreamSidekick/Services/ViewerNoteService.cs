using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Keeps private, lightweight context about regular viewers. Nothing is sent to Twitch and the
/// local JSON file contains only names, the streamer's note, and simple activity timestamps.
/// </summary>
public sealed class ViewerNoteService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<string, ViewerNote> _viewers = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRoutineSave = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPrunedAt = DateTimeOffset.MinValue;

    public ViewerNoteService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSidekick");
        _path = Path.Combine(directory, "viewer-notes.json");
        Load();
    }

    public IReadOnlyList<ViewerNote> GetAll()
    {
        lock (_sync)
        {
            return _viewers.Values
                .OrderByDescending(viewer => !string.IsNullOrWhiteSpace(viewer.Note))
                .ThenByDescending(viewer => viewer.LastSeenAt)
                .Take(250)
                .ToList();
        }
    }

    public ViewerNote SetNote(ViewerNoteRequest request)
    {
        var name = NormalizeName(request.UserName);
        var note = request.Note?.Trim() ?? "";
        if (note.Length > 500) throw new ArgumentException("Viewer notes can contain up to 500 characters.");
        lock (_sync)
        {
            var existing = GetOrCreateUnsafe(name);
            var updated = existing with { Note = note, UpdatedAt = DateTimeOffset.UtcNow };
            _viewers[name] = updated;
            SaveUnsafe();
            return updated;
        }
    }

    public bool Delete(string userName)
    {
        var name = NormalizeName(userName);
        lock (_sync)
        {
            var removed = _viewers.Remove(name);
            if (removed) SaveUnsafe();
            return removed;
        }
    }

    public void Observe(string userName, string activity, bool important = false)
    {
        if (string.IsNullOrWhiteSpace(userName)) return;
        var name = NormalizeName(userName);
        lock (_sync)
        {
            var existing = GetOrCreateUnsafe(name);
            _viewers[name] = existing with
            {
                LastSeenAt = DateTimeOffset.UtcNow,
                LastActivity = activity,
                ActivityCount = existing.ActivityCount + 1
            };
            PruneRoutineViewerHistoryUnsafe();
            // Busy chat should not turn into constant disk traffic. Notable events save immediately.
            if (important || DateTimeOffset.UtcNow - _lastRoutineSave >= TimeSpan.FromSeconds(30)) SaveUnsafe();
        }
    }

    private ViewerNote GetOrCreateUnsafe(string name) => _viewers.TryGetValue(name, out var existing)
        ? existing
        : new ViewerNote(name, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "First seen in chat", 0, DateTimeOffset.UtcNow);

    private static string NormalizeName(string value)
    {
        var name = value.Trim().TrimStart('@');
        if (name.Length is < 1 or > 50) throw new ArgumentException("Enter a valid Twitch viewer name.");
        return name;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var viewers = JsonSerializer.Deserialize<List<ViewerNote>>(File.ReadAllText(_path), JsonOptions) ?? [];
            foreach (var viewer in viewers) _viewers[viewer.UserName] = viewer;
        }
        catch
        {
            // Notes are optional; retain dashboard availability if the file was manually damaged.
        }
    }

    private void SaveUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_viewers.Values.OrderBy(viewer => viewer.UserName), JsonOptions));
        File.Move(temporary, _path, true);
        _lastRoutineSave = DateTimeOffset.UtcNow;
    }

    private void PruneRoutineViewerHistoryUnsafe()
    {
        var now = DateTimeOffset.UtcNow;
        if (_viewers.Count <= 2500 || now - _lastPrunedAt < TimeSpan.FromHours(1)) return;
        _lastPrunedAt = now;

        // Manual notes are never removed automatically. Only old, unannotated chat sightings are
        // eligible, which keeps useful viewer context while putting a ceiling on passive history.
        var staleCutoff = now.AddDays(-90);
        foreach (var name in _viewers.Values
                     .Where(viewer => string.IsNullOrWhiteSpace(viewer.Note) && viewer.LastSeenAt < staleCutoff)
                     .Select(viewer => viewer.UserName)
                     .ToList())
            _viewers.Remove(name);

        if (_viewers.Count <= 5000) return;
        foreach (var name in _viewers.Values
                     .Where(viewer => string.IsNullOrWhiteSpace(viewer.Note))
                     .OrderBy(viewer => viewer.LastSeenAt)
                     .Take(_viewers.Count - 5000)
                     .Select(viewer => viewer.UserName)
                     .ToList())
            _viewers.Remove(name);
    }
}

public sealed record ViewerNote(
    string UserName,
    string Note,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string LastActivity,
    int ActivityCount,
    DateTimeOffset UpdatedAt);

public sealed record ViewerNoteRequest(string UserName, string? Note);
