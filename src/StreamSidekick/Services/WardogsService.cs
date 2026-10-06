using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamSidekick.Services;

/// <summary>
/// Reads opt-in community-server statistics from WARDOGS Tracker. The provider is deliberately
/// dormant until the streamer saves a SteamID64 and server slug/join code.
/// </summary>
public sealed partial class WardogsService
{
    private const string ApiBase = "https://wardogstracker.gg/api/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _settingsPath;
    private readonly object _sync = new();
    private WardogsSettings _settings;
    private string _lastStatus;

    public WardogsService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamSidekick");
        _settingsPath = Path.Combine(directory, "wardogs-settings.json");
        _settings = ReadSettings();
        _lastStatus = IsConfigured(_settings)
            ? "Ready to read the configured community-server leaderboard."
            : "Add a SteamID64 and community server to enable post-stream results.";
    }

    public bool Configured
    {
        get { lock (_sync) return IsConfigured(_settings); }
    }

    public WardogsProviderStatus GetStatus()
    {
        lock (_sync)
        {
            return new WardogsProviderStatus(
                IsConfigured(_settings),
                NullIfEmpty(_settings.SteamId),
                NullIfEmpty(_settings.Server),
                _lastStatus);
        }
    }

    public WardogsProviderStatus SaveSettings(WardogsSettingsRequest request)
    {
        var steamId = request.SteamId?.Trim() ?? "";
        var server = request.Server?.Trim() ?? "";
        if (steamId.Length > 0 && !SteamIdPattern().IsMatch(steamId))
            throw new ArgumentException("SteamID64 must contain exactly 17 digits.");
        if (server.Length > 0 && !ServerPattern().IsMatch(server))
            throw new ArgumentException("Use the Wardogs Tracker server slug, server ID, or join code.");
        if ((steamId.Length == 0) != (server.Length == 0))
            throw new ArgumentException("Enter both the SteamID64 and Wardogs community server, or clear both fields.");

        lock (_sync)
        {
            _settings = new WardogsSettings(steamId, server);
            _lastStatus = IsConfigured(_settings)
                ? "Settings saved. The next tracked session will capture a starting baseline."
                : "Wardogs post-stream results are disabled.";
            WriteSettings(_settings);
            return GetStatusUnsafe();
        }
    }

    public async Task<WardogsStatSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        WardogsSettings settings;
        lock (_sync) settings = _settings;
        if (!IsConfigured(settings))
            throw new InvalidOperationException("Configure a SteamID64 and Wardogs community server first.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var serverPath = Uri.EscapeDataString(settings.Server);
        var url = $"{ApiBase}/servers/{serverPath}/leaderboard?range=24h&limit=100";
        try
        {
            using var response = await _httpClientFactory.CreateClient().GetAsync(url, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("WARDOGS Tracker could not find that community server or its kill feed is unavailable.");
            if ((int)response.StatusCode == 429)
                throw new InvalidOperationException("WARDOGS Tracker is temporarily rate limited. Try again in a minute.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var leaderboard = await JsonSerializer.DeserializeAsync<WardogsLeaderboardResponse>(stream, JsonOptions, timeout.Token)
                ?? throw new InvalidOperationException("WARDOGS Tracker returned an empty response.");
            var player = leaderboard.Players?.FirstOrDefault(item => item.SteamId == settings.SteamId)
                ?? throw new InvalidOperationException("That SteamID64 was not found in the server's top 100 players for the last 24 hours.");
            var serverName = string.IsNullOrWhiteSpace(leaderboard.Server?.Name)
                ? settings.Server
                : leaderboard.Server.Name;
            var sourceUrl = !string.IsNullOrWhiteSpace(leaderboard.Server?.Url)
                ? leaderboard.Server.Url
                : $"https://wardogstracker.gg/servers/{serverPath}";
            var snapshot = new WardogsStatSnapshot(
                DateTimeOffset.UtcNow,
                player.SteamId,
                string.IsNullOrWhiteSpace(player.Name) ? "Wardogs player" : player.Name,
                settings.Server,
                serverName,
                Math.Max(0, player.Kills),
                Math.Max(0, player.Deaths),
                Math.Max(0, player.Headshots),
                Math.Max(0, player.Kd),
                player.LongestKillM,
                NullIfEmpty(player.TopWeapon),
                "WARDOGS Tracker",
                sourceUrl);
            lock (_sync) _lastStatus = $"Latest 24-hour stats fetched for {snapshot.PlayerName}.";
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync) _lastStatus = "WARDOGS Tracker did not respond within eight seconds.";
            throw new InvalidOperationException(_lastStatus);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_sync) _lastStatus = exception.Message;
            throw;
        }
    }

    private WardogsSettings ReadSettings()
    {
        try
        {
            return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<WardogsSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new WardogsSettings()
                : new WardogsSettings();
        }
        catch (JsonException)
        {
            return new WardogsSettings();
        }
        catch (IOException)
        {
            return new WardogsSettings();
        }
    }

    private void WriteSettings(WardogsSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = _settingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private WardogsProviderStatus GetStatusUnsafe() => new(
        IsConfigured(_settings),
        NullIfEmpty(_settings.SteamId),
        NullIfEmpty(_settings.Server),
        _lastStatus);

    private static bool IsConfigured(WardogsSettings settings) =>
        SteamIdPattern().IsMatch(settings.SteamId) && ServerPattern().IsMatch(settings.Server);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("^[0-9]{17}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerPattern();

    private sealed record WardogsSettings(string SteamId = "", string Server = "");
    private sealed record WardogsLeaderboardResponse(WardogsServer? Server, List<WardogsPlayer>? Players);
    private sealed record WardogsServer(string? Name, string? Url);
    private sealed record WardogsPlayer(
        string SteamId,
        string? Name,
        int Kills,
        int Deaths,
        double Kd,
        int Headshots,
        double? LongestKillM,
        string? TopWeapon);
}
