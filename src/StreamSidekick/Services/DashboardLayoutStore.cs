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

    private static IReadOnlyList<string> Normalize(IEnumerable<string> values) => values
        .Select(value => value?.Trim() ?? "")
        .Where(value => value.Length is > 0 and <= 80)
        .Distinct(StringComparer.Ordinal)
        .Take(50)
        .ToList();
}
