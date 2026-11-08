using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Games;
using TennisSms.Core.Players;
using TennisSms.Core.Time;
using TennisSms.Web.Games;
using TennisSms.Web.Infrastructure;
using TennisSms.Web.Players;
using TennisSms.Web.Sms;
using Wolverine;
using Wolverine.Persistence.Durability;

namespace TennisSms.Web.Simulator;

/// <summary>
/// A text (or organizer action) waiting to happen: an auto-reply "typing", or a scenario step.
/// Steps due at the same instant are released together.
/// </summary>
public sealed record PendingStep(Guid Id, string? Phone, string Label, DateTimeOffset DueAt, Func<Task> Run);

/// <summary>
/// Drives the fake phones (spec §11.3): seeding, auto-replies, the pending-reply queue with
/// pause and step, and scenarios. Every text a fake phone sends goes through
/// <see cref="SimulatorGateway.ReceiveAsync"/>, so the real inbound pipeline handles it.
/// </summary>
public sealed class SimulatorEngine : IDisposable
{
    private readonly SimulatorGateway _gateway;
    private readonly IServiceScopeFactory _scopes;
    private readonly IDocumentStore _store;
    private readonly GameClock _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<SimulatorEngine> _log;
    private readonly object _gate = new();
    private readonly List<PendingStep> _pending = [];
    private readonly ConcurrentDictionary<string, AutoReplyMode> _modes = new();

    // Reset, JOIN all, Quick game and scenarios run one at a time: a JOIN that arrives
    // mid-reset would otherwise find no player and get the unknown-number reply.
    private readonly SemaphoreSlim _setup = new(1, 1);

    public SimulatorEngine(
        SimulatorGateway gateway, IServiceScopeFactory scopes, IDocumentStore store, GameClock clock,
        TimeProvider time, IOptions<TennisSmsOptions> options, ILogger<SimulatorEngine> log)
    {
        (_gateway, _scopes, _store, _clock, _time, _log) = (gateway, scopes, store, clock, time, log);
        Phones = SimulatorPhones.All(options.Value.Organizer.PhoneE164, options.Value.Organizer.Name);
        _gateway.TextSent += OnTextSent;
    }

    public IReadOnlyList<SimPhone> Phones { get; }

    public DateTime NowLocal => _clock.ToLocal(_time.GetUtcNow());

    /// <summary>Auto-replies wait a random time in this range, shown as "typing…".</summary>
    public TimeSpan MinDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>For "YES then OUT": how long after "You're in" the OUT goes.</summary>
    public TimeSpan OutAfter { get; set; } = TimeSpan.FromSeconds(20);

    public bool Paused { get; private set; }

    /// <summary>True while a setup action (reset, join, quick game, scenario) is running.</summary>
    public bool Busy => _setup.CurrentCount == 0;
    public string? Status { get; private set; }

    /// <summary>Raised when anything the page shows changes outside the database (queue, modes, status).</summary>
    public event Action? Changed;

    public IReadOnlyList<PendingStep> Pending
    {
        get { lock (_gate) return _pending.OrderBy(p => p.DueAt).ToList(); }
    }

    public AutoReplyMode ModeOf(string phone) => _modes.GetValueOrDefault(phone, AutoReplyMode.Manual);

    public void SetMode(string phone, AutoReplyMode mode)
    {
        _modes[phone] = mode;
        Changed?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        Changed?.Invoke();
    }

    /// <summary>A fake phone texts the service.</summary>
    public Task SendAsync(string phone, string body) => _gateway.ReceiveAsync(phone, body);

    public void Queue(string? phone, string label, TimeSpan after, Func<Task> run)
    {
        lock (_gate) _pending.Add(new PendingStep(Guid.NewGuid(), phone, label, _time.GetUtcNow() + after, run));
        Changed?.Invoke();
    }

    /// <summary>Releases everything that's due, together. Called by <see cref="SimulatorLoop"/>.</summary>
    public Task ReleaseDueAsync()
    {
        if (Paused) return Task.CompletedTask;
        var now = _time.GetUtcNow();
        return RunAsync(Take(p => p.DueAt <= now));
    }

    /// <summary>Releases the next pending step (and anything due at the same instant), paused or not.</summary>
    public Task StepAsync()
    {
        DateTimeOffset? next;
        lock (_gate) next = _pending.Count == 0 ? null : _pending.Min(p => p.DueAt);
        return next is null ? Task.CompletedTask : RunAsync(Take(p => p.DueAt == next));
    }

    private List<PendingStep> Take(Func<PendingStep, bool> which)
    {
        lock (_gate)
        {
            var due = _pending.Where(which).ToList();
            foreach (var p in due) _pending.Remove(p);
            return due;
        }
    }

    private async Task RunAsync(List<PendingStep> steps)
    {
        if (steps.Count == 0) return;
        Changed?.Invoke();
        await Task.WhenAll(steps.Select(async s =>
        {
            try { await s.Run(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Simulator step '{Label}' failed", s.Label);
                SetStatus($"{s.Label} failed: {ex.Message}");
            }
        }));
    }

    private void SetStatus(string? status)
    {
        Status = status;
        Changed?.Invoke();
    }

    // ---------- Auto-replies ----------

    private void OnTextSent(SimulatedText text)
    {
        var mode = ModeOf(text.To);
        if (mode is AutoReplyMode.Manual or AutoReplyMode.Silent) return;

        var reply = AutoReplies.ReplyTo(text.Body, mode, Random.Shared);
        if (reply is not null)
            Queue(text.To, $"auto: {reply}", RandomDelay(), () => SendAsync(text.To, reply));

        if (mode == AutoReplyMode.YesThenOut && text.Body.StartsWith("You're in", StringComparison.Ordinal))
            Queue(text.To, "auto: OUT", OutAfter, () => SendAsync(text.To, "OUT"));
    }

    private TimeSpan RandomDelay()
    {
        var spread = Math.Max(0, (MaxDelay - MinDelay).TotalMilliseconds);
        return MinDelay + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * spread);
    }

    // ---------- Seed and reset ----------

    /// <summary>Wipes all data (events, documents, pending Wolverine messages) and seeds the 7 players as Pending, plus the organizer (opted in).</summary>
    public Task ResetAsync() => Exclusive(ResetCoreAsync);

    private async Task ResetCoreAsync()
    {
        lock (_gate) _pending.Clear();
        _modes.Clear();

        await using (var scope = _scopes.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IMessageStore>().Admin.ClearAllAsync();

        await _store.Advanced.CleanAllDocumentsAsync();
        await _store.Advanced.CleanAllEventDataAsync();

        foreach (var phone in SimulatorPhones.Players)
            await InvokeAsync(new RegisterPlayer(Guid.NewGuid(), phone.Name, phone.Number));
        await InvokeAsync(new EnsureOrganizerPlayer()); // as on app start

        SetStatus("Reset: 7 players seeded (not joined yet), plus the organizer.");
    }

    /// <summary>Every seeded player texts JOIN.</summary>
    public Task JoinAllAsync() => Exclusive(JoinAllCoreAsync);

    private async Task JoinAllCoreAsync()
    {
        foreach (var phone in SimulatorPhones.Players)
            await SendAsync(phone.Number, "JOIN");

        // Wait for the inbound queue to opt them in, so a Quick game right after invites everyone.
        SetStatus(await WaitForAllJoinedAsync(TimeSpan.FromSeconds(30))
            ? "All players texted JOIN and are opted in."
            : "All players texted JOIN; some aren't opted in yet (were they seeded? try Reset).");
    }

    public async Task<bool> WaitForAllJoinedAsync(TimeSpan timeout)
    {
        var until = _time.GetUtcNow() + timeout;
        while (_time.GetUtcNow() < until)
        {
            var players = await LoadPlayersAsync();
            if (players.Count >= SimulatorPhones.Players.Count && players.All(p => p.ConsentStatus == ConsentStatus.OptedIn))
                return true;
            await Task.Delay(100);
        }
        return false;
    }

    /// <summary>A doubles game ~20 hours out (inside the cancellation window), inviting everyone opted in.</summary>
    public async Task<Guid> QuickGameAsync()
    {
        var id = Guid.Empty;
        await Exclusive(async () => id = await QuickGameCoreAsync());
        return id;
    }

    private async Task<Guid> QuickGameCoreAsync()
    {
        var soon = _clock.ToLocal(_time.GetUtcNow()).AddHours(20);
        var start = new DateTime(soon.Year, soon.Month, soon.Day, soon.Hour, 0, 0).AddHours(1);
        var id = Guid.NewGuid();
        await InvokeAsync(new ScheduleGame(id, GameFormat.Doubles, start, start.AddHours(1.5), "Oak Park courts"));

        var optedIn = (await LoadPlayersAsync()).Where(p => p.ConsentStatus == ConsentStatus.OptedIn).Select(p => p.Id).ToArray();
        if (optedIn.Length > 0) await InvokeAsync(new AddAndInvite(id, [], optedIn));

        SetStatus(optedIn.Length == 0
            ? "Quick game created, but no one is opted in yet. Use JOIN all first."
            : $"Quick game created and {optedIn.Length} players invited.");
        return id;
    }

    // ---------- Scenarios ----------

    /// <summary>
    /// Resets, joins everyone, sets up the scenario's games and queues its steps (so they show
    /// as "typing…" and respect pause/step). With <paramref name="runNow"/>, steps run
    /// immediately in order instead; headless tests use that to wait for each step.
    /// </summary>
    public Task RunScenarioAsync(SimulatorScenario scenario, Func<Func<Task>, Task>? runNow = null) =>
        Exclusive(() => RunScenarioCoreAsync(scenario, runNow));

    private async Task RunScenarioCoreAsync(SimulatorScenario scenario, Func<Func<Task>, Task>? runNow)
    {
        SetStatus($"Scenario '{scenario.Name}': resetting…");
        await ResetCoreAsync();
        await JoinAllCoreAsync();
        if ((await LoadPlayersAsync()).Any(p => p.ConsentStatus != ConsentStatus.OptedIn))
        {
            SetStatus($"Scenario '{scenario.Name}': players didn't all join in time.");
            return;
        }

        var context = new ScenarioContext(this, await LoadPlayersAsync());
        var steps = await scenario.Build(context);

        if (runNow is null)
        {
            foreach (var step in steps)
                Queue(step.Phone is null ? null : context.Phone(step.Phone), step.Label, step.At, step.Run);
        }
        else
        {
            foreach (var group in steps.GroupBy(s => s.At).OrderBy(g => g.Key))
                await runNow(() => Task.WhenAll(group.Select(s => s.Run())));
        }

        SetStatus($"Scenario '{scenario.Name}' running: {scenario.Description}");
    }

    // ---------- Helpers ----------

    private async Task Exclusive(Func<Task> action)
    {
        await _setup.WaitAsync();
        Changed?.Invoke();
        try { await action(); }
        finally
        {
            _setup.Release();
            Changed?.Invoke();
        }
    }

    public async Task InvokeAsync(object command)
    {
        await using var scope = _scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(command);
    }

    public async Task<T> InvokeAsync<T>(object command)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command);
    }

    private async Task<List<Player>> LoadPlayersAsync()
    {
        await using var session = _store.QuerySession();
        return (await session.Query<Player>().ToListAsync()).ToList();
    }

    public void Dispose()
    {
        _gateway.TextSent -= OnTextSent;
        _setup.Dispose();
    }
}

/// <summary>What an auto-reply mode says to a text. Null means no reply.</summary>
public static class AutoReplies
{
    public static string? ReplyTo(string body, AutoReplyMode mode, Random random)
    {
        var isInvite = body.Contains("Reply YES or NO", StringComparison.Ordinal);
        var isWhichGame = body.StartsWith("Which game?", StringComparison.Ordinal);
        if (!isInvite && !isWhichGame) return null;

        // Random: YES 50%, NO 25%, no reply 25%.
        var roll = random.NextDouble();
        if (isWhichGame)
            return mode is AutoReplyMode.Random && roll >= 0.75 ? null : "1";

        return mode switch
        {
            AutoReplyMode.AlwaysYes or AutoReplyMode.YesThenOut => "YES",
            AutoReplyMode.AlwaysNo => "NO",
            AutoReplyMode.Random => roll < 0.5 ? "YES" : roll < 0.75 ? "NO" : null,
            _ => null,
        };
    }
}

/// <summary>Releases due simulator steps a few times a second.</summary>
public sealed class SimulatorLoop(SimulatorEngine engine) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await engine.ReleaseDueAsync();
    }
}
