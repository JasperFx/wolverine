using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// GH-5545. A supervised run that OWNS SQL Server or the Azure Service Bus emulator (the ownership GH-5532 set up, see
// build/FreshContainers.cs) recreates that service in the middle of the run when the run degrades on it, then carries on.
//
// The cases this is for: a SQL Server spinning in RESOURCE MONITOR under Docker VM memory pressure, every query waiting
// to compile, and an emulator whose SQL backend piled up state until a 2 second test took 2 minutes. Both answered their
// readiness checks and every test finished inside the 5 minute stall threshold, so nothing failed: the run crawled.
//
// Two signals have to agree before anything is recreated:
//
//   1. Bobcat's (JasperFx/bobcat#824): the last 5 completed tests with a known duration each ran more than 10x it, or a
//      test stalled. That pauses the run: the supervisor kills the workers so nothing is mid-test.
//   2. Ours, specific to the service: a probe of the service itself, every 15 seconds while the run goes. The last 3
//      probes must all be unhealthy -- slower than the service's threshold, or failing, or (SQL Server) requests
//      waiting on the compile or memory grant semaphores. Slow tests on a healthy service are not the service's fault,
//      and recreating it would not help.
//
// This file is the part that decides, with no Nuke or Bobcat in it, so build/../src/Testing/BuildTests can test it. The
// wiring is in FreshContainers.cs (the recreate itself), SupervisedTests.cs (CI targets) and LocalGate.cs (the gate).

/// <summary>One probe of a service: how long it took to answer and, when it did not answer properly, why.</summary>
sealed record ServiceProbe(DateTimeOffset At, TimeSpan Latency, string Problem = null);

/// <summary>Whether a service's own probes say it is degraded, and the probes that say so.</summary>
sealed record ServiceVerdict(string Service, bool Degraded, string Evidence);

/// <summary>
/// Probes one service in the background and keeps the recent results. <see cref="Verdict"/> is degraded only when
/// the last <see cref="RequiredSamples"/> probes, all taken in the last <see cref="MaxSampleAge"/>, were unhealthy.
/// </summary>
sealed class ServiceHealthWatch
{
    /// <summary>Consecutive unhealthy probes it takes. At the default interval that is 45 seconds of it.</summary>
    public const int RequiredSamples = 3;

    const int Kept = 20;

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    /// <summary>A probe older than this says nothing about the service now.</summary>
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromMinutes(2);

    readonly Func<CancellationToken, Task<ServiceProbe>> _probe;
    readonly TimeProvider _time;
    readonly List<ServiceProbe> _samples = [];
    readonly object _lock = new();
    int _started;

    public ServiceHealthWatch(string service, TimeSpan latencyThreshold, Func<CancellationToken, Task<ServiceProbe>> probe,
        TimeProvider time = null)
    {
        Service = service;
        LatencyThreshold = latencyThreshold;
        _probe = probe;
        _time = time ?? TimeProvider.System;
    }

    public string Service { get; }

    /// <summary>A probe slower than this is unhealthy.</summary>
    public TimeSpan LatencyThreshold { get; }

    public TimeSpan Interval { get; init; } = DefaultInterval;

    /// <summary>The wait between the probes <see cref="Confirm"/> takes itself.</summary>
    public TimeSpan ConfirmSpacing { get; init; } = TimeSpan.FromSeconds(2);

    public bool IsUnhealthy(ServiceProbe probe) => probe.Problem is not null || probe.Latency > LatencyThreshold;

    public void Record(ServiceProbe probe)
    {
        lock (_lock)
        {
            _samples.Add(probe);
            if (_samples.Count > Kept) _samples.RemoveAt(0);
        }
    }

    /// <summary>Forget every probe: after a recreate, what the old container answered says nothing.</summary>
    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }

    ServiceProbe[] recent()
    {
        var since = _time.GetUtcNow() - MaxSampleAge;
        lock (_lock) return _samples.Where(x => x.At >= since).ToArray();
    }

    public ServiceVerdict Verdict()
    {
        var recent = this.recent();
        if (recent.Length < RequiredSamples)
        {
            return new ServiceVerdict(Service, false,
                $"only {recent.Length} probe(s) of {Service} in the last {MaxSampleAge.TotalMinutes:0} minutes, {RequiredSamples} needed");
        }

        var last = recent.TakeLast(RequiredSamples).ToArray();
        var described = string.Join("; ", last.Select(describe));

        return last.All(IsUnhealthy)
            ? new ServiceVerdict(Service, true, $"the last {RequiredSamples} probes of {Service} were all unhealthy: {described}")
            : new ServiceVerdict(Service, false, $"the last {RequiredSamples} probes of {Service} were not all unhealthy: {described}");
    }

    string describe(ServiceProbe probe)
    {
        var latency = probe.Latency.TotalSeconds >= 1 ? $"{probe.Latency.TotalSeconds:0.0}s" : $"{probe.Latency.TotalMilliseconds:0}ms";
        if (probe.Problem is not null) return $"{latency}, {probe.Problem}";
        return probe.Latency > LatencyThreshold
            ? $"{latency}, over the {LatencyThreshold.TotalSeconds:0}s threshold"
            : $"{latency}, healthy";
    }

    /// <summary>
    /// The verdict, probing now until there are <see cref="RequiredSamples"/> recent probes when the background probe
    /// has not taken that many yet (a run that degraded within a minute of starting).
    /// </summary>
    public async Task<ServiceVerdict> Confirm(CancellationToken ct)
    {
        while (recent().Length < RequiredSamples)
        {
            Record(await probeSafely(ct));
            if (recent().Length < RequiredSamples) await Task.Delay(ConfirmSpacing, _time, ct);
        }

        return Verdict();
    }

    async Task<ServiceProbe> probeSafely(CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        try
        {
            return await _probe(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new ServiceProbe(started, _time.GetUtcNow() - started, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Probe every <see cref="Interval"/> until the token is cancelled. Once per watch; later calls do nothing.</summary>
    public void Start(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Record(await probeSafely(ct));
                    await Task.Delay(Interval, _time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);
    }
}

enum HealAction
{
    /// <summary>Recreate the service.</summary>
    Heal,

    /// <summary>Leave the service alone and let the run go on: it is not ours, or its probes do not confirm it.</summary>
    Decline,

    /// <summary>The service is degraded and may not be recreated now: stop the run rather than let it crawl.</summary>
    Abort
}

sealed record HealDecision(HealAction Action, string Reason, int Heal = 0);

/// <summary>Thrown by <see cref="ServiceHealer.HealNow"/> when the service may not be recreated.</summary>
sealed class HealRefusedException(string message) : Exception(message);

/// <summary>
/// The rules: only a run that owns a service recreates it, only when its own probes confirm it is degraded, never
/// while another program is using it, and at most <see cref="MaxHealsPerService"/> times in one run.
/// </summary>
sealed class ServiceHealPolicy
{
    /// <summary>
    /// The same number as Bobcat's <c>Track.MaxHeals</c> default. A gate runs many units on one service, and the state
    /// that degrades it piles up across all of them, so one heal early and one late are both plausible. A third
    /// degradation is the service saying something a restart does not fix.
    /// </summary>
    public const int MaxHealsPerService = 2;

    readonly Dictionary<string, string> _owned = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _heals = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();

    /// <summary>This run started the service fresh (GH-5532), so it may recreate it.</summary>
    public void Own(string service, string why)
    {
        lock (_lock) _owned[service] = why;
    }

    public bool Owns(string service)
    {
        lock (_lock) return _owned.ContainsKey(service);
    }

    public int HealsOf(string service)
    {
        lock (_lock) return _heals.GetValueOrDefault(service);
    }

    /// <summary>
    /// What to do about <paramref name="service"/>, given its probes' verdict and <paramref name="whyNotNow"/>: null
    /// when nothing else is using it now, otherwise why it may not be recreated.
    /// </summary>
    public HealDecision Decide(string service, ServiceVerdict verdict, Func<string> whyNotNow)
    {
        if (!Owns(service))
        {
            return new HealDecision(HealAction.Decline,
                $"this run did not start {service} fresh, so it never recreates it (--no-fresh-containers, someone else's " +
                "container, or one that was in use when the run started)");
        }

        if (verdict is not { Degraded: true })
        {
            return new HealDecision(HealAction.Decline,
                $"{service}'s own probes do not confirm it is degraded, so recreating it would not help: {verdict?.Evidence ?? "no verdict"}");
        }

        return CanHealNow(service, whyNotNow) is { } refused
            ? new HealDecision(HealAction.Abort, $"{service} is degraded ({verdict.Evidence}), but {refused}")
            : new HealDecision(HealAction.Heal, verdict.Evidence, HealsOf(service) + 1);
    }

    /// <summary>Null when the service may be recreated now, otherwise why not. Ownership, the cap, then the guards.</summary>
    public string CanHealNow(string service, Func<string> whyNotNow)
    {
        if (!Owns(service)) return $"this run does not own {service}";

        var healed = HealsOf(service);
        if (healed >= MaxHealsPerService)
        {
            return $"it was already recreated {healed} time(s) in this run (at most {MaxHealsPerService}), " +
                   "and a degradation a restart did not fix needs a person";
        }

        return whyNotNow() is { } reason ? $"it may not be recreated now: {reason}" : null;
    }

    /// <summary>Count a heal that ran. Returns which heal of the service this was.</summary>
    public int RecordHeal(string service)
    {
        lock (_lock)
        {
            var number = _heals.GetValueOrDefault(service) + 1;
            _heals[service] = number;
            return number;
        }
    }
}

/// <summary>
/// Puts <see cref="ServiceHealthWatch"/> and <see cref="ServiceHealPolicy"/> together around the build's recreate.
/// </summary>
sealed class ServiceHealer
{
    /// <summary>
    /// Degradations a run may report that its services' probes do not confirm before it stops pausing for them. Each
    /// one costs the tests that were in flight (Bobcat kills the workers before asking), so a run that is slow for some
    /// other reason must not be paused every 5 tests to the end.
    /// </summary>
    public const int MaxUnconfirmedPerRun = 2;

    readonly Func<string, string> _whyNotNow;
    readonly Func<string, CancellationToken, Task> _recreate;
    readonly Action<string> _log;
    readonly Dictionary<string, ServiceHealthWatch> _watches = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="whyNotNow">Null when nothing else uses the service now, otherwise why it may not be recreated.</param>
    /// <param name="recreate">Recreates the service's containers and waits until they answer; throws on failure.</param>
    /// <param name="log">Warnings: every heal is logged loudly.</param>
    public ServiceHealer(ServiceHealPolicy policy, Func<string, string> whyNotNow,
        Func<string, CancellationToken, Task> recreate, Action<string> log)
    {
        Policy = policy;
        _whyNotNow = whyNotNow;
        _recreate = recreate;
        _log = log;
    }

    public ServiceHealPolicy Policy { get; }

    public void Watch(ServiceHealthWatch watch)
    {
        lock (_watches) _watches[watch.Service] = watch;
    }

    public ServiceHealthWatch WatchOf(string service)
    {
        lock (_watches) return _watches.GetValueOrDefault(service);
    }

    public async Task<ServiceVerdict> Confirm(string service, CancellationToken ct)
    {
        return WatchOf(service) is { } watch
            ? await watch.Confirm(ct)
            : new ServiceVerdict(service, false, $"{service} has no probe");
    }

    /// <summary>
    /// Confirm <paramref name="service"/> is degraded, decide, and recreate it when the rules allow. The decision says
    /// what happened; an <see cref="HealAction.Abort"/> is the caller's to act on.
    /// </summary>
    public async Task<HealDecision> HealIfDegraded(string service, string signal, Func<CancellationToken, Task> afterRecreate,
        CancellationToken ct)
    {
        var verdict = await Confirm(service, ct);
        var decision = Policy.Decide(service, verdict, () => _whyNotNow(service));
        if (decision.Action != HealAction.Heal) return decision;

        try
        {
            var number = await HealNow(service, signal, verdict.Evidence, afterRecreate, ct);
            return decision with { Heal = number };
        }
        catch (HealRefusedException e)
        {
            return new HealDecision(HealAction.Abort, e.Message);
        }
    }

    /// <summary>
    /// Recreate <paramref name="service"/> now, re-checking ownership, the cap and the guards first, then run
    /// <paramref name="afterRecreate"/> (the lane databases a recreated SQL Server has lost). Throws
    /// <see cref="HealRefusedException"/> when the rules do not allow it. Returns which heal of the service this was.
    /// </summary>
    public async Task<int> HealNow(string service, string signal, string evidence, Func<CancellationToken, Task> afterRecreate,
        CancellationToken ct)
    {
        if (Policy.CanHealNow(service, () => _whyNotNow(service)) is { } refused)
        {
            throw new HealRefusedException($"Not recreating {service}: {refused}");
        }

        var number = Policy.HealsOf(service) + 1;
        _log($"!!!! GH-5545: RECREATING {service} IN THE MIDDLE OF THE RUN (heal {number} of at most {ServiceHealPolicy.MaxHealsPerService}) !!!!");
        _log($"     why: {signal}");
        if (evidence is not null) _log($"     {evidence}");

        // Counted before the recreate: a recreate that fails part way has still thrown the old container away.
        Policy.RecordHeal(service);
        await _recreate(service, ct);
        if (afterRecreate is not null) await afterRecreate(ct);
        WatchOf(service)?.Reset();

        _log($"!!!! GH-5545: {service} recreated and answering (heal {number}); the run resumes and the tests that were " +
             "stopped run again !!!!");
        return number;
    }
}

/// <summary>Which services a LocalGate run may recreate mid-run.</summary>
static class GateHealing
{
    /// <summary>
    /// Whether the gate may recreate SQL Server: only when no two of its units use it at the same time. Bobcat keeps
    /// the units on one track apart, but most suites use SQL Server through a database of their own and hold no track
    /// for it, so in a full gate they run beside each other, and recreating the server under one of them fails it.
    /// That holds when one suite uses SQL Server, or when every suite that uses it holds the sqlserver track.
    /// </summary>
    /// <param name="whyNot">Why not, when there are SQL Server suites; null otherwise.</param>
    public static bool HealsSqlServer(IReadOnlyList<(string Suite, bool UsesSqlServer, bool HoldsSqlServerTrack)> suites,
        out string whyNot)
    {
        whyNot = null;
        var users = suites.Where(x => x.UsesSqlServer).ToArray();
        if (users.Length == 0) return false;
        if (users.Length == 1 || users.All(x => x.HoldsSqlServerTrack)) return true;

        var beside = users.Where(x => !x.HoldsSqlServerTrack).Select(x => x.Suite).ToArray();
        whyNot = $"{users.Length} suites use it, and {string.Join(", ", beside)} hold no track for it, so they run beside " +
                 "the others and recreating it would fail them. A gate with one SQL Server suite (--gate-only) recreates it.";
        return false;
    }

    /// <summary>The service a heal on the gate's <paramref name="track"/> recreates, or null for a track that only reports.</summary>
    /// <remarks>
    /// The Azure Service Bus suite holds asb-emulator and asb-emulator-2. A heal recreates both pairs, so only the first
    /// track carries it: Bobcat heals every healable track a unit holds, and two would recreate them twice.
    /// </remarks>
    public static string ServiceHealedBy(string track) => track switch
    {
        "sqlserver" => "sqlserver",
        "asb-emulator" => "asb",
        _ => null
    };
}
