using System.Text.Json;

namespace StreamSidekick.Services;

public sealed record ConnectionDiagnostic(DateTimeOffset At, string Service, string Level, string Message);

/// <summary>
/// Keeps a small, secret-free connection history for troubleshooting. Tokens and passwords must never
/// be passed to Record; messages should describe only the service, endpoint, and corrective action.
/// </summary>
public sealed class ConnectionDiagnosticsLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "logs",
        "connections.jsonl");

    public void Record(string service, string level, string message)
    {
        var entry = new ConnectionDiagnostic(DateTimeOffset.UtcNow, service, level, message);
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);
            TrimUnsafe();
        }
    }

    public IReadOnlyList<ConnectionDiagnostic> GetRecent(int count = 30)
    {
        lock (_sync)
        {
            if (!File.Exists(_path)) return [];
            return File.ReadLines(_path)
                .TakeLast(Math.Clamp(count, 1, 100))
                .Select(line =>
                {
                    try { return JsonSerializer.Deserialize<ConnectionDiagnostic>(line, JsonOptions); }
                    catch { return null; }
                })
                .Where(entry => entry is not null)
                .Select(entry => entry!)
                .Reverse()
                .ToList();
        }
    }

    private void TrimUnsafe()
    {
        var file = new FileInfo(_path);
        if (file.Length < 256 * 1024) return;
        // Connection events are rare; retaining 300 lines is ample while permanently bounding disk use.
        var retained = File.ReadLines(_path).TakeLast(300).ToList();
        File.WriteAllLines(_path, retained);
    }
}
