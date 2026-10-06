using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Owns automatic session start/finalization in the local service rather than the browser tab. This
/// keeps reports safe when the dashboard is closed, refreshed, or left unattended after a stream.
/// </summary>
public sealed class StreamSessionLifecycleService(
    ObsWebSocketClient obs,
    SessionStore sessions,
    WardogsService wardogs,
    ILogger<StreamSessionLifecycleService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private bool _observedLiveOutput;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        obs.EventReceived += HandleObsEvent;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (obs.IsConnected)
                {
                    try
                    {
                        await ObserveAsync(await obs.GetStreamActiveAsync(stoppingToken), stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogDebug(exception, "Could not read OBS stream state for session tracking.");
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            obs.EventReceived -= HandleObsEvent;
        }
    }

    private void HandleObsEvent(string eventType, JsonElement data)
    {
        if (!eventType.Equals("StreamStateChanged", StringComparison.Ordinal)
            || !data.TryGetProperty("outputActive", out var active)) return;

        _ = ObserveSafelyAsync(active.GetBoolean());
    }

    private async Task ObserveSafelyAsync(bool active)
    {
        try { await ObserveAsync(active, CancellationToken.None); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not update the automatic stream session."); }
    }

    private async Task ObserveAsync(bool active, CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (active)
            {
                if (!_observedLiveOutput)
                {
                    var alreadyTracking = sessions.HasActiveSession;
                    var session = await sessions.StartOrResumeRecentAsync(null, cancellationToken);
                    await sessions.AddActivityAsync(
                        "live",
                        alreadyTracking ? "OBS stream started; existing session continued." : "OBS stream started; session tracking began automatically.",
                        cancellationToken);
                    if (wardogs.Configured && session.Wardogs?.Baseline is null)
                    {
                        try
                        {
                            await sessions.SetActiveWardogsBaselineAsync(
                                await wardogs.FetchSnapshotAsync(cancellationToken),
                                cancellationToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            logger.LogWarning(exception, "Could not capture the Wardogs starting baseline.");
                        }
                    }
                }
                _observedLiveOutput = true;
                return;
            }

            if (!_observedLiveOutput) return;
            _observedLiveOutput = false;
            if (!sessions.HasActiveSession) return;

            await sessions.AddActivityAsync("offline", "OBS stream ended; session finalized automatically.", cancellationToken);
            await sessions.EndAsync(cancellationToken);
        }
        finally
        {
            _stateGate.Release();
        }
    }
}
