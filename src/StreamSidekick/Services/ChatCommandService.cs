using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamSidekick.Services;

/// <summary>
/// Stores simple viewer commands locally and applies cooldowns before a reply reaches Twitch.
/// Commands intentionally remain plain text so they are easy to inspect, back up, and edit.
/// </summary>
public sealed partial class ChatCommandService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Dictionary<string, ChatCommandDefinition> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _globalUses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _viewerUses = new(StringComparer.OrdinalIgnoreCase);
    private int _cooldownChecksUntilCleanup = 256;

    public ChatCommandService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamSidekick");
        _path = Path.Combine(directory, "chat-commands.json");
        Load();
    }

    public IReadOnlyList<ChatCommandDefinition> GetAll()
    {
        lock (_sync) return _commands.Values.OrderBy(command => command.Command).ToList();
    }

    public ChatCommandDefinition Upsert(ChatCommandUpsertRequest request)
    {
        var command = NormalizeCommand(request.Command);
        var response = request.Response?.Trim() ?? "";
        if (response.Length is < 1 or > 450) throw new ArgumentException("Enter a response between 1 and 450 characters.");
        var definition = new ChatCommandDefinition(
            command,
            response,
            request.Enabled,
            Math.Clamp(request.CooldownSeconds, 5, 300),
            DateTimeOffset.UtcNow);
        lock (_sync)
        {
            _commands[command] = definition;
            SaveUnsafe();
        }
        return definition;
    }

    public bool Delete(string command)
    {
        var normalized = NormalizeCommand(command);
        lock (_sync)
        {
            var removed = _commands.Remove(normalized);
            if (removed) SaveUnsafe();
            return removed;
        }
    }

    public string? TryCreateResponse(TwitchChatMessage message)
    {
        var command = FirstCommand(message.Text);
        if (command is null) return null;
        lock (_sync)
        {
            if (!_commands.TryGetValue(command, out var definition) || !definition.Enabled) return null;
            if (!TryAcquireCooldownUnsafe(command, message.UserId, definition.CooldownSeconds, message.IsBroadcaster)) return null;
            return definition.Response.Replace("{user}", message.UserName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Shares the same anti-spam rules with built-in commands such as !song.</summary>
    public bool TryAcquireBuiltInCooldown(string command, TwitchChatMessage message, int cooldownSeconds = 15)
    {
        lock (_sync) return TryAcquireCooldownUnsafe(command, message.UserId, cooldownSeconds, message.IsBroadcaster);
    }

    public static string? FirstCommand(string? text)
    {
        var first = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || !first.StartsWith('!')) return null;
        try { return NormalizeCommand(first); }
        catch (ArgumentException) { return null; }
    }

    private bool TryAcquireCooldownUnsafe(string command, string userId, int seconds, bool bypass)
    {
        if (bypass) return true;
        var now = DateTimeOffset.UtcNow;
        if (--_cooldownChecksUntilCleanup <= 0)
        {
            // Cooldowns only last five minutes at most. Clearing older viewer keys occasionally
            // keeps a long-running channel from retaining one dictionary entry per past chatter.
            var cutoff = now.AddMinutes(-6);
            foreach (var key in _viewerUses.Where(item => item.Value < cutoff).Select(item => item.Key).ToList())
                _viewerUses.Remove(key);
            foreach (var key in _globalUses.Where(item => item.Value < cutoff).Select(item => item.Key).ToList())
                _globalUses.Remove(key);
            _cooldownChecksUntilCleanup = 256;
        }
        var viewerKey = $"{command}:{userId}";
        if (_globalUses.TryGetValue(command, out var global) && now - global < TimeSpan.FromSeconds(seconds)) return false;
        if (_viewerUses.TryGetValue(viewerKey, out var viewer) && now - viewer < TimeSpan.FromSeconds(Math.Max(30, seconds))) return false;
        _globalUses[command] = now;
        _viewerUses[viewerKey] = now;
        return true;
    }

    private static string NormalizeCommand(string? value)
    {
        var command = (value ?? "").Trim().ToLowerInvariant();
        if (!command.StartsWith('!')) command = $"!{command}";
        if (!CommandPattern().IsMatch(command))
            throw new ArgumentException("Commands must start with ! and contain only letters, numbers, underscores, or hyphens.");
        return command;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var commands = JsonSerializer.Deserialize<List<ChatCommandDefinition>>(File.ReadAllText(_path), JsonOptions) ?? [];
            foreach (var command in commands) _commands[command.Command] = command;
        }
        catch
        {
            // A malformed optional file must not prevent the streaming dashboard from starting.
        }
    }

    private void SaveUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_commands.Values.OrderBy(command => command.Command), JsonOptions));
        File.Move(temporary, _path, true);
    }

    [GeneratedRegex("^![a-z0-9][a-z0-9_-]{0,30}$", RegexOptions.IgnoreCase)]
    private static partial Regex CommandPattern();
}

public sealed record ChatCommandDefinition(
    string Command,
    string Response,
    bool Enabled,
    int CooldownSeconds,
    DateTimeOffset UpdatedAt);

public sealed record ChatCommandUpsertRequest(
    string Command,
    string Response,
    bool Enabled = true,
    int CooldownSeconds = 15);
