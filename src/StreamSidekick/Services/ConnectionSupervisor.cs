namespace StreamSidekick.Services;

/// <summary>
/// Reconnects OBS and Twitch after startup or a transient disconnect. Each integration has its own
/// retry clock so an offline program does not cause a tight loop or interfere with the other services.
/// </summary>
public sealed class ConnectionSupervisor(
    ObsWebSocketClient obs,
    TwitchClient twitch,
    TwitchEventSubService twitchEvents,
    ConnectionDiagnosticsLog diagnostics,
    ILogger<ConnectionSupervisor> logger) : BackgroundService
{
    private DateTimeOffset _nextObsAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextTwitchAttempt = DateTimeOffset.MinValue;
    private bool _obsFailureLogged;
    private bool _twitchFailureLogged;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            if (!obs.IsConnected && obs.HasSavedConnection && now >= _nextObsAttempt)
            {
                try
                {
                    await obs.ConnectSavedAsync(stoppingToken);
                    _nextObsAttempt = now.AddSeconds(5);
                    diagnostics.Record("OBS", "success", "Automatically restored the saved OBS WebSocket connection.");
                    _obsFailureLogged = false;
                }
                catch (Exception exception)
                {
                    _nextObsAttempt = now.AddSeconds(15);
                    if (!_obsFailureLogged)
                        diagnostics.Record("OBS", "warning", $"Automatic reconnect is waiting for OBS: {FriendlyMessage(exception)}");
                    _obsFailureLogged = true;
                    logger.LogDebug(exception, "Saved OBS connection is not available yet.");
                }
            }

            var configuration = twitch.GetConfiguration();
            if (configuration is not null && now >= _nextTwitchAttempt)
            {
                try
                {
                    // Renewable device-flow tokens are rotated before expiry even while EventSub is healthy.
                    configuration = await twitch.GetValidConfigurationAsync(stoppingToken);
                    var wasDisconnected = !twitchEvents.GetSnapshot().Connected;
                    if (!wasDisconnected)
                    {
                        _nextTwitchAttempt = now.AddMinutes(1);
                        _twitchFailureLogged = false;
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                    await twitch.ValidateConfigurationAsync(configuration, stoppingToken);
                    await twitchEvents.ConnectAsync(configuration, stoppingToken);
                    _nextTwitchAttempt = now.AddMinutes(1);
                    diagnostics.Record("Twitch", "success", "Automatically restored Twitch alerts and Channel Point events.");
                    _twitchFailureLogged = false;
                }
                catch (Exception exception)
                {
                    // A temporary network failure gets another chance; an expired token remains visible in preflight.
                    _nextTwitchAttempt = now.AddMinutes(1);
                    if (!_twitchFailureLogged)
                        diagnostics.Record("Twitch", "warning", $"Automatic reconnect failed: {FriendlyMessage(exception)}");
                    _twitchFailureLogged = true;
                    logger.LogDebug(exception, "Saved Twitch connection is not available yet.");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private static string FriendlyMessage(Exception exception) =>
        exception.GetBaseException().Message.ReplaceLineEndings(" ");
}
