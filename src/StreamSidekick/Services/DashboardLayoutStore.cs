using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Persists card order outside the browser so layout survives app restarts and browser changes.
/// The existing StreamSidekick data directory is retained to preserve all pre-rename user data.
/// </summary>
public sealed class DashboardLayoutStore
{
    private static readonly string LayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "dashboard-layout.json");
    private static readonly string PagesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "dashboard-pages.json");
    private static readonly HashSet<string> AllowedPages = new(StringComparer.Ordinal)
    {
        "pre", "during", "after"
    };
    private readonly object _sync = new();

    public IReadOnlyList<string> Read()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(LayoutPath)) return [];
                var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(LayoutPath)) ?? [];
                return Normalize(saved);
            }
            catch
            {
                return [];
            }
        }
    }

    public IReadOnlyList<string> Save(IReadOnlyList<string>? panelOrder)
    {
        var normalized = Normalize(panelOrder ?? []);
        if (normalized.Count == 0) throw new ArgumentException("Dashboard layout must contain at least one card.");
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            var temporary = LayoutPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, LayoutPath, true);
        }
        return normalized;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadPages()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(PagesPath)) return new Dictionary<string, IReadOnlyList<string>>();
                var saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(PagesPath));
                return NormalizePages(saved?.Select(entry =>
                    new KeyValuePair<string, IReadOnlyList<string>>(entry.Key, entry.Value)));
            }
            catch
            {
                return new Dictionary<string, IReadOnlyList<string>>();
            }
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> SavePages(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? panelPages)
    {
        var normalized = NormalizePages(panelPages);
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PagesPath)!);
            var temporary = PagesPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PagesPath, true);
        }
        return normalized;
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values) => values
        .Select(value => value?.Trim() ?? "")
        .Where(value => value.Length is > 0 and <= 80)
        .Distinct(StringComparer.Ordinal)
        .Take(50)
        .ToList();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizePages(
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? values)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in values ?? [])
        {
            var panelId = entry.Key?.Trim() ?? "";
            if (panelId.Length is < 1 or > 80) continue;
            var pages = (entry.Value ?? [])
                .Select(page => page?.Trim() ?? "")
                .Where(AllowedPages.Contains)
                .Distinct(StringComparer.Ordinal)
                .Take(AllowedPages.Count)
                .ToList();
            if (pages.Count > 0) result[panelId] = pages;
            if (result.Count >= 50) break;
        }
        return result;
    }
}
