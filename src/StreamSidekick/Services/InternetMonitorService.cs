using System.Diagnostics;
using System.Net.Sockets;

namespace StreamSidekick.Services;

/// <summary>
/// Performs a tiny TCP connection check every five seconds. Unlike a speed test, this transfers no
/// meaningful payload, so monitoring cannot compete with the stream for bandwidth. OBS output
/// congestion remains the authoritative signal for insufficient upload capacity.
/// </summary>
public sealed class InternetMonitorService(SessionStore sessions, ILogger<InternetMonitorService> logger)
    : BackgroundService
{
    private sealed record ProbeResult(DateTimeOffset At, bool Online, double? LatencyMs, string Target);

    private readonly object _sync = new();
    private readonly Queue<ProbeResult> _history = new();
    private readonly Queue<InternetConnectionEvent> _events = new();
    private InternetConnectionSnapshot _current = new(
        DateTimeOffset.UtcNow,
        false,
        null,
        0,
        "checking",
        "Waiting for the first connection check.",
        []);
    private string _previousStatus = "checking";
    private int _slowStreak;
    private int _healthyLatencyStreak;

    public InternetConnectionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _current with { Events = _events.Reverse().ToList() };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var probe = await ProbeAsync(stoppingToken);
                InternetConnectionEvent? notableEvent;
                lock (_sync)
                {
                    _history.Enqueue(probe);
                    while (_history.Count > 120) _history.Dequeue(); // Ten minutes at five-second intervals.

                    var recent = _history.Where(item => probe.At - item.At <= TimeSpan.FromMinutes(1)).ToList();
                    var failures = recent.Count(item => !item.Online);
                    var failurePercent = recent.Count == 0 ? 0 : failures * 100d / recent.Count;
                    UpdateLatencyStreaks(probe);
                    var status = DetermineStatus(probe, failures);
                    var detail = DescribeCurrent(probe, status, failures);
                    notableEvent = CreateEvent(probe, status, failures);
                    if (notableEvent is not null)
                    {
                        _events.Enqueue(notableEvent);
                        while (_events.Count > 40) _events.Dequeue();
                    }

                    _previousStatus = status;
                    _current = new InternetConnectionSnapshot(
                        probe.At,
                        probe.Online,
                        probe.LatencyMs,
                        Math.Round(failurePercent, 1),
                        status,
                        detail,
                        []);
                }

                // Session activities make connection hiccups available in the post-stream timeline too.
                if (notableEvent is not null)
                    await sessions.AddActivityAsync("internet", notableEvent.Message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Internet connection sample failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private string DetermineStatus(ProbeResult probe, int recentFailures)
    {
        if (!probe.Online) return "offline";
        if (recentFailures >= 2) return "unstable";
        // Two readings enter or leave the warning state. This preserves real sustained hiccups while
        // preventing a connection near 120 ms from creating hundreds of slow/recovered timeline rows.
        if (_previousStatus == "slow") return _healthyLatencyStreak >= 2 ? "healthy" : "slow";
        if (_slowStreak >= 2) return "slow";
        return "healthy";
    }

    private void UpdateLatencyStreaks(ProbeResult probe)
    {
        if (!probe.Online)
        {
            _slowStreak = 0;
            _healthyLatencyStreak = 0;
            return;
        }

        if (probe.LatencyMs >= 120)
        {
            _slowStreak++;
            _healthyLatencyStreak = 0;
        }
        else if (probe.LatencyMs < 100)
        {
            _healthyLatencyStreak++;
            _slowStreak = 0;
        }
        else
        {
            _slowStreak = 0;
            _healthyLatencyStreak = 0;
        }
    }

    private static string DescribeCurrent(ProbeResult probe, string status, int recentFailures) => status switch
    {
        "offline" => "The latest internet check failed.",
        "unstable" => $"{recentFailures} checks failed during the last minute.",
        "slow" => $"Latency is high at {probe.LatencyMs:0} ms.",
        _ => $"Online through {probe.Target} at {probe.LatencyMs:0} ms."
    };

    private InternetConnectionEvent? CreateEvent(ProbeResult probe, string status, int recentFailures)
    {
        if (status == _previousStatus) return null;
        if (_previousStatus == "checking" && status == "healthy") return null;

        var message = status switch
        {
            "offline" => "Internet connection check failed.",
            "unstable" => $"Internet connection is unstable: {recentFailures} failed checks in the last minute.",
            "slow" => $"High internet latency detected: {probe.LatencyMs:0} ms.",
            "healthy" => $"Internet connection recovered at {probe.LatencyMs:0} ms.",
            _ => "Internet connection status changed."
        };
        var level = status == "offline" ? "error" : status is "slow" or "unstable" ? "warning" : "recovered";
        return new InternetConnectionEvent(probe.At, level, message, probe.LatencyMs);
    }

    private static async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        // A second destination prevents one provider-specific routing issue from looking like a full outage.
        var primary = await TryConnectAsync("1.1.1.1", "Cloudflare", cancellationToken);
        if (primary.Online) return primary;
        return await TryConnectAsync("www.twitch.tv", "Twitch", cancellationToken);
    }

    private static async Task<ProbeResult> TryConnectAsync(
        string host,
        string label,
        CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new TcpClient();
            await client.ConnectAsync(host, 443, timeout.Token);
            timer.Stop();
            return new ProbeResult(at, true, Math.Round(timer.Elapsed.TotalMilliseconds, 1), label);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProbeResult(at, false, null, label);
        }
    }
}
