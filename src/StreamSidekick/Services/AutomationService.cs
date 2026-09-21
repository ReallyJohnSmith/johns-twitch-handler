using System.Text.Json;

namespace StreamSidekick.Services;

/// <summary>
/// Runs a deliberately small set of streamer-approved actions. There is no shell, script, URL,
/// or plug-in execution here, so importing a typo cannot become arbitrary code execution.
/// </summary>
public sealed class AutomationService(
    TwitchClient twitch,
    ObsWebSocketClient obs,
    SessionStore sessions,
    ILogger<AutomationService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "automations.json");
    private readonly Dictionary<Guid, AutomationDefinition> _definitions = [];

    public IReadOnlyList<AutomationDefinition> GetAll()
    {
        EnsureLoaded();
        lock (_sync) return _definitions.Values.OrderBy(item => item.Name).ToList();
    }

    public AutomationDefinition Upsert(AutomationUpsertRequest request)
    {
        EnsureLoaded();
        var name = Required(request.Name, "Automation name", 80);
        var trigger = NormalizeChoice(request.TriggerType, ["manual", "interval"], "trigger");
        var action = NormalizeChoice(request.ActionType, ["chat-message", "switch-scene", "marker"], "action");
        var value = Required(request.Value, "Action value", action == "chat-message" ? 450 : 128);
        var interval = trigger == "interval" ? Math.Clamp(request.IntervalMinutes, 5, 240) : 0;
        var id = request.Id is { } requestedId && requestedId != Guid.Empty ? requestedId : Guid.NewGuid();

        lock (_sync)
        {
            _definitions.TryGetValue(id, out var existing);
            var definition = new AutomationDefinition(
                id,
                name,
                request.Enabled,
                trigger,
                interval,
                action,
                value,
                existing?.LastRunAt ?? (trigger == "interval" ? DateTimeOffset.UtcNow : null),
                existing?.LastResult ?? "Not run yet");
            _definitions[id] = definition;
            SaveUnsafe();
            return definition;
        }
    }

    public bool Delete(Guid id)
    {
        EnsureLoaded();
        lock (_sync)
        {
            var removed = _definitions.Remove(id);
            if (removed) SaveUnsafe();
            return removed;
        }
    }

    public async Task<AutomationDefinition> RunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        AutomationDefinition definition;
        lock (_sync)
        {
            if (!_definitions.TryGetValue(id, out definition!))
                throw new KeyNotFoundException("Automation not found.");
        }

        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            var result = await ExecuteActionAsync(definition, cancellationToken);
            return RecordResult(definition.Id, result);
        }
        catch (Exception exception)
        {
            RecordResult(definition.Id, $"Failed: {exception.GetBaseException().Message}");
            throw;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureLoaded();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!sessions.HasActiveSession) continue;
            var now = DateTimeOffset.UtcNow;
            List<Guid> due;
            lock (_sync)
            {
                due = _definitions.Values
                    .Where(item => item.Enabled
                        && item.TriggerType == "interval"
                        && item.LastRunAt is { } last
                        && now - last >= TimeSpan.FromMinutes(item.IntervalMinutes))
                    .Select(item => item.Id)
                    .ToList();
            }

            foreach (var id in due)
            {
                try { await RunAsync(id, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { logger.LogWarning(exception, "Scheduled automation {AutomationId} failed.", id); }
            }
        }
    }

    private async Task<string> ExecuteActionAsync(AutomationDefinition definition, CancellationToken cancellationToken)
    {
        switch (definition.ActionType)
        {
            case "chat-message":
                await twitch.SendChatMessageAsync(definition.Value, cancellationToken);
                return "Sent to Twitch chat";
            case "switch-scene":
                if (!obs.IsConnected) throw new InvalidOperationException("OBS is not connected.");
                await obs.SetCurrentSceneAsync(definition.Value, cancellationToken);
                return $"Changed scene to {definition.Value}";
            case "marker":
                if (!sessions.HasActiveSession) throw new InvalidOperationException("Start a Handler session before adding a marker.");
                await sessions.AddMarkerAsync(definition.Value, "automation", false, "Created by a safe automation", cancellationToken);
                return "Added a stream marker";
            default:
                throw new InvalidOperationException("This automation action is not supported.");
        }
    }

    private AutomationDefinition RecordResult(Guid id, string result)
    {
        lock (_sync)
        {
            var current = _definitions[id];
            var updated = current with { LastRunAt = DateTimeOffset.UtcNow, LastResult = result };
            _definitions[id] = updated;
            SaveUnsafe();
            return updated;
        }
    }

    private bool _loaded;
    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_path)) return;
                var items = JsonSerializer.Deserialize<List<AutomationDefinition>>(File.ReadAllText(_path), JsonOptions) ?? [];
                foreach (var item in items) _definitions[item.Id] = item;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not load local automations.");
            }
        }
    }

    private void SaveUnsafe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = $"{_path}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_definitions.Values.OrderBy(item => item.Name), JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static string Required(string? value, string label, int maximumLength)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0) throw new ArgumentException($"{label} is required.");
        if (normalized.Length > maximumLength) throw new ArgumentException($"{label} must be {maximumLength} characters or fewer.");
        return normalized;
    }

    private static string NormalizeChoice(string? value, string[] choices, string label)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        if (!choices.Contains(normalized, StringComparer.Ordinal))
            throw new ArgumentException($"Unsupported automation {label}.");
        return normalized;
    }
}

public sealed record AutomationDefinition(
    Guid Id,
    string Name,
    bool Enabled,
    string TriggerType,
    int IntervalMinutes,
    string ActionType,
    string Value,
    DateTimeOffset? LastRunAt,
    string LastResult);

public sealed record AutomationUpsertRequest(
    Guid? Id,
    string? Name,
    bool Enabled,
    string? TriggerType,
    int IntervalMinutes,
    string? ActionType,
    string? Value);
