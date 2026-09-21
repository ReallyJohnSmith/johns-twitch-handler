using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Combines a manual raid shortlist with inbound and outbound history. Live status is refreshed from
/// Twitch only when the panel asks for it, avoiding a permanent polling load on the stream PC.
/// </summary>
public sealed class RaidManagerService(TwitchClient twitch)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "raid-channels.json");
    private readonly string _historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "raid-history.json");
    private readonly Dictionary<string, RaidChannel> _channels = LoadChannels();
    private readonly List<RaidEvent> _history = LoadHistory();

    public RaidChannel AddSuggested(string channelName)
    {
        var name = NormalizeChannel(channelName);
        lock (_sync)
        {
            var channel = _channels.TryGetValue(name, out var existing)
                ? existing with { Suggested = true }
                : new RaidChannel(name, name, true, null, null, null, false, null, null, null);
            _channels[name] = channel;
            SaveUnsafe();
            return channel;
        }
    }

    public void RecordIncoming(string channelName, string? detail)
    {
        var name = NormalizeChannel(channelName);
        lock (_sync)
        {
            var existing = _channels.TryGetValue(name, out var channel)
                ? channel
                : new RaidChannel(name, name, false, null, null, null, false, null, null, null);
            _channels[name] = existing with
            {
                DisplayName = channelName,
                LastRaidedYouAt = DateTimeOffset.UtcNow,
                LastInboundDetail = detail
            };
            _history.Add(new RaidEvent(Guid.NewGuid(), "incoming", name, channelName, DateTimeOffset.UtcNow, detail));
            SaveUnsafe();
        }
    }

    public RaidChannel MarkRaided(string channelName)
    {
        var name = NormalizeChannel(channelName);
        lock (_sync)
        {
            var existing = _channels.TryGetValue(name, out var channel)
                ? channel
                : new RaidChannel(name, name, false, null, null, null, false, null, null, null);
            var updated = existing with { LastRaidedAt = DateTimeOffset.UtcNow };
            _channels[name] = updated;
            _history.Add(new RaidEvent(Guid.NewGuid(), "outgoing", name, existing.DisplayName, DateTimeOffset.UtcNow, null));
            SaveUnsafe();
            return updated;
        }
    }

    public bool Remove(string channelName)
    {
        var name = NormalizeChannel(channelName);
        lock (_sync)
        {
            var removed = _channels.Remove(name);
            if (removed) SaveUnsafe();
            return removed;
        }
    }

    public async Task<RaidManagerSnapshot> GetSnapshotAsync(bool refresh, int days, CancellationToken cancellationToken = default)
    {
        List<RaidChannel> channels;
        lock (_sync) channels = _channels.Values.ToList();
        string? warning = null;
        if (refresh && channels.Count > 0)
        {
            try
            {
                var live = await twitch.GetLiveChannelsAsync(channels.Select(channel => channel.ChannelName), cancellationToken);
                var byName = live.ToDictionary(item => item.ChannelName, StringComparer.OrdinalIgnoreCase);
                lock (_sync)
                {
                    foreach (var channel in channels)
                    {
                        var status = byName.GetValueOrDefault(channel.ChannelName);
                        _channels[channel.ChannelName] = channel with
                        {
                            IsLive = status is not null,
                            StreamTitle = status?.Title,
                            GameName = status?.GameName,
                            ViewerCount = status?.ViewerCount
                        };
                    }
                    SaveUnsafe();
                    channels = _channels.Values.ToList();
                }
            }
            catch (Exception exception)
            {
                warning = exception.GetBaseException().Message;
            }
        }
        var safeDays = days switch { 7 => 7, 14 => 14, _ => 31 };
        List<RaidEvent> history;
        lock (_sync)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-safeDays);
            history = _history.Where(item => item.At >= cutoff).OrderByDescending(item => item.At).ToList();
        }
        return new RaidManagerSnapshot(
            channels.OrderByDescending(channel => channel.IsLive)
                .ThenByDescending(channel => channel.Suggested)
                .ThenByDescending(channel => channel.LastRaidedYouAt ?? channel.LastRaidedAt)
                .ToList(),
            history,
            safeDays,
            warning);
    }

    private static Dictionary<string, RaidChannel> LoadChannels()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSidekick", "raid-channels.json");
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var channels = JsonSerializer.Deserialize<List<RaidChannel>>(File.ReadAllText(path), JsonOptions) ?? [];
            return channels.ToDictionary(channel => channel.ChannelName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static List<RaidEvent> LoadHistory()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSidekick", "raid-history.json");
        try
        {
            if (!File.Exists(path))
            {
                // Preserve the most recent inbound/outbound timestamps from the original channel-only format.
                return LoadChannels().Values.SelectMany(channel => new RaidEvent?[]
                    {
                        channel.LastRaidedYouAt is { } incoming
                            ? new RaidEvent(Guid.NewGuid(), "incoming", channel.ChannelName, channel.DisplayName, incoming, channel.LastInboundDetail)
                            : null,
                        channel.LastRaidedAt is { } outgoing
                            ? new RaidEvent(Guid.NewGuid(), "outgoing", channel.ChannelName, channel.DisplayName, outgoing, null)
                            : null
                    })
                    .Where(item => item is not null)
                    .Cast<RaidEvent>()
                    .OrderBy(item => item.At)
                    .ToList();
            }
            return JsonSerializer.Deserialize<List<RaidEvent>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch { return []; }
    }

    private void SaveUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_channels.Values.OrderBy(channel => channel.ChannelName), JsonOptions));
        File.Move(temporary, _path, true);
        // Raid records are tiny, but keep a generous ceiling so years of ordinary use remain useful.
        if (_history.Count > 5000) _history.RemoveRange(0, _history.Count - 5000);
        var historyTemporary = _historyPath + ".tmp";
        File.WriteAllText(historyTemporary, JsonSerializer.Serialize(_history.OrderBy(item => item.At), JsonOptions));
        File.Move(historyTemporary, _historyPath, true);
    }

    private static string NormalizeChannel(string value)
    {
        var name = (value ?? "").Trim().TrimStart('@');
        if (Uri.TryCreate(name, UriKind.Absolute, out var uri) && uri.Host.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
            name = uri.AbsolutePath.Trim('/').Split('/')[0];
        if (name.Length is < 1 or > 50 || name.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
            throw new ArgumentException("Enter a Twitch channel name, such as yepitsjohn.");
        return name.ToLowerInvariant();
    }
}

public sealed record TwitchLiveChannel(string ChannelName, string DisplayName, string Title, string GameName, int ViewerCount);

public sealed record RaidChannel(
    string ChannelName,
    string DisplayName,
    bool Suggested,
    DateTimeOffset? LastRaidedAt,
    DateTimeOffset? LastRaidedYouAt,
    string? LastInboundDetail,
    bool IsLive,
    string? StreamTitle,
    string? GameName,
    int? ViewerCount);

public sealed record RaidEvent(Guid Id, string Direction, string ChannelName, string DisplayName, DateTimeOffset At, string? Detail);
public sealed record RaidManagerSnapshot(IReadOnlyList<RaidChannel> Channels, IReadOnlyList<RaidEvent> History, int Days, string? Warning);
public sealed record RaidChannelRequest(string ChannelName);
