using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace StreamSidekick.Services;

internal sealed record StoredObsConnection(string Url, string? Password);
internal sealed record StoredTwitchConnection(
    string ClientId,
    string AccessToken,
    string BroadcasterId,
    string? ChannelName,
    string? RefreshToken = null,
    DateTimeOffset ExpiresAt = default);
internal sealed record StoredSpotifyConnection(
    string ClientId,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt);
internal sealed record StoredConnections(
    StoredObsConnection? Obs = null,
    StoredTwitchConnection? Twitch = null,
    StoredSpotifyConnection? Spotify = null);

/// <summary>
/// Persists connection credentials as one ASP.NET Data Protection payload. The key ring is protected
/// with Windows DPAPI, so the file can only be decrypted by this Windows account on this computer.
/// </summary>
public sealed class SecureConnectionStore(IDataProtectionProvider provider, ILogger<SecureConnectionStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    // The launcher normally enforces one instance, but a brief overlap during restart must not let
    // one process overwrite credentials that another process just added.
    private readonly Mutex _processMutex = new(false, "Local\\StreamSidekick.ConnectionCredentials");
    private readonly IDataProtector _protector = provider.CreateProtector("StreamSidekick.ConnectionCredentials.v1");
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "connections.protected");

    internal StoredConnections Read()
    {
        return WithCredentialLock(ReadUnsafe);
    }

    internal void SaveObs(StoredObsConnection connection) => Update(settings => settings with { Obs = connection });
    internal void SaveTwitch(StoredTwitchConnection connection) => Update(settings => settings with { Twitch = connection });
    internal void SaveSpotify(StoredSpotifyConnection connection) => Update(settings => settings with { Spotify = connection });
    internal void ClearTwitch() => Update(settings => settings with { Twitch = null });
    internal void ClearSpotify() => Update(settings => settings with { Spotify = null });

    private void Update(Func<StoredConnections, StoredConnections> update)
    {
        WithCredentialLock(() =>
        {
            var settings = update(ReadUnsafe());
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            var backup = _path + ".backup";
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(temporary, _protector.Protect(json));
                if (File.Exists(_path)) File.Copy(_path, backup, true);
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        });
    }

    private StoredConnections ReadUnsafe()
    {
        if (!File.Exists(_path)) return new StoredConnections();
        try
        {
            var json = _protector.Unprotect(File.ReadAllText(_path));
            return JsonSerializer.Deserialize<StoredConnections>(json, JsonOptions) ?? new StoredConnections();
        }
        catch (Exception exception)
        {
            // Recover the preceding atomic snapshot before asking the user to reconnect everything.
            var backup = _path + ".backup";
            if (File.Exists(backup))
            {
                try
                {
                    var json = _protector.Unprotect(File.ReadAllText(backup));
                    logger.LogWarning(exception, "The newest credential snapshot was unreadable; using its encrypted backup.");
                    return JsonSerializer.Deserialize<StoredConnections>(json, JsonOptions) ?? new StoredConnections();
                }
                catch (Exception backupException)
                {
                    logger.LogWarning(backupException, "The encrypted credential backup could not be read either.");
                }
            }
            // Corrupt data should never prevent startup. The user can reconnect to replace it safely.
            logger.LogWarning(exception, "Saved connection credentials could not be read.");
            return new StoredConnections();
        }
    }

    private T WithCredentialLock<T>(Func<T> action)
    {
        lock (_sync)
        {
            var ownsMutex = false;
            try
            {
                try { ownsMutex = _processMutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) throw new TimeoutException("Another John's Twitch Handler copy is updating saved connections. Try again in a moment.");
                return action();
            }
            finally
            {
                if (ownsMutex) _processMutex.ReleaseMutex();
            }
        }
    }
}
