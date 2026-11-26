using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Bobcat.Supervisor;
using Serilog;

// GH-5545: the build's side of recreating a degraded SQL Server or Azure Service Bus emulator mid-run. The rules and
// the reasoning are at the top of build/ServiceHeal.cs; this file is the probes, the recreate and the wiring into
// Bobcat's supervisor (Supervisor.OnDegraded for the CI targets, Track.Heal for LocalGate).
partial class Build
{
    /// <summary>Who owns what, and how many times each has been recreated, for the life of this build process.</summary>
    static readonly ServiceHealPolicy HealPolicy = new();

    /// <summary>The background probes run until the build process exits.</summary>
    static readonly CancellationTokenSource _healWatchesStop = new();

    ServiceHealer _healer;

    ServiceHealer healer => _healer ??= new ServiceHealer(HealPolicy, whyNotHealNow,
        (service, ct) => Task.Run(() => recreateForHeal(service), ct),
        message => Log.Warning("{Message}", message));

    /// <summary>A probe slower than this is unhealthy. A healthy server answers either in milliseconds.</summary>
    static readonly TimeSpan SqlServerProbeThreshold = TimeSpan.FromSeconds(3);

    static readonly TimeSpan AzureServiceBusProbeThreshold = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts probing <paramref name="service"/> (sqlserver or asb) in the background, once per build process. Only
    /// for a service this run owns: there is nothing to decide about one it never recreates.
    /// </summary>
    void watchForHealing(string service)
    {
        if (healer.WatchOf(service) is not null) return;

        var watch = service switch
        {
            "sqlserver" => new ServiceHealthWatch(service, SqlServerProbeThreshold, probeSqlServer),
            "asb" => new ServiceHealthWatch(service, AzureServiceBusProbeThreshold, probeAzureServiceBus),
            _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Only sqlserver and asb are healed")
        };

        healer.Watch(watch);
        watch.Start(_healWatchesStop.Token);
        Log.Information("GH-5545: probing {Service} every {Seconds}s; it is recreated mid-run only if the run degrades " +
                        "and {Required} probes in a row are slower than {Threshold}s or fail",
            service, (int)watch.Interval.TotalSeconds, ServiceHealthWatch.RequiredSamples, (int)watch.LatencyThreshold.TotalSeconds);
    }

    /// <summary>
    /// A fresh connection (so the login is measured too) and one query for the symptom we saw: requests waiting on
    /// the compile or memory grant semaphores. Runs from this process, so its session never counts as "in use".
    /// </summary>
    async Task<ServiceProbe> probeSqlServer(CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();

        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerConnectionString)
            {
                Pooling = false,
                ConnectTimeout = 15
            }.ConnectionString);
        await conn.OpenAsync(ct);

        await using var command = conn.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText =
            "select count(*) from sys.dm_exec_requests " +
            "where wait_type in ('RESOURCE_SEMAPHORE_QUERY_COMPILE', 'RESOURCE_SEMAPHORE') and wait_time >= 5000";
        var waiting = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

        return new ServiceProbe(at, clock.Elapsed,
            waiting > 0 ? $"{waiting} request(s) waiting 5s or more to compile or for a memory grant" : null);
    }

    static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// The emulator's management api, which reads from its SQL backend: that backend piling up state is what slowed
    /// the suite to 2 minutes a test. Both pairs when the second is running, since a heal recreates both.
    /// </summary>
    static async Task<ServiceProbe> probeAzureServiceBus(CancellationToken ct)
    {
        var at = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var problems = new List<string>();

        foreach (var port in portAnswers(5301) ? new[] { 5300, 5301 } : [5300])
        {
            using var response = await _probeHttp.GetAsync($"http://localhost:{port}/$Resources/topics", ct);

            // Not 503 is the same "answering" WaitForAzureServiceBusEmulatorToBeReady uses: 200 locally, 400 in CI.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                problems.Add($"the management api on {port} answered 503");
        }

        return new ServiceProbe(at, clock.Elapsed, problems.Count > 0 ? string.Join(", ", problems) : null);
    }

    /// <summary>
    /// Null when nothing else uses the service now: the same guards as a fresh start (GH-5532). This run's own workers
    /// were killed a moment ago by the supervisor, and their processes and SQL Server sessions take a few seconds to go,
    /// so a reason is re-checked for up to 30 seconds before it stands.
    /// </summary>
    string whyNotHealNow(string service)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var reason = whyNotFresh(service, containersOf(service));
            if (reason is null || deadline.Elapsed > TimeSpan.FromSeconds(30)) return reason;
            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// The recreate itself, under the same cross-process lock as a fresh start, with the guards checked again inside it.
    /// </summary>
    void recreateForHeal(string service)
    {
        var recreate = containersOf(service);
        using var freshLock = acquireFreshLock(service);

        if (whyNotFresh(service, recreate) is { } reason)
            throw new HealRefusedException($"Not recreating {service}: {reason}");

        Log.Warning("GH-5545: recreating {Containers} with new volumes", string.Join(" ", recreate));
        recreateContainers(recreate, freshLock);
    }

    /// <summary>
    /// The CI targets that own a service (CISqlServer*, CIPolecat*, CIAzureServiceBus*): when the run degrades, heal
    /// each owned service its own probes confirm, then resume. <paramref name="afterRecreate"/> re-creates what the
    /// recreated service lost (the lane databases).
    /// </summary>
    void healDegradedServices(Supervisor supervisor, string projectName, string[] services,
        Func<string, Func<CancellationToken, Task>> afterRecreate)
    {
        var owned = services.Where(HealPolicy.Owns).ToArray();
        foreach (var service in services.Except(owned))
        {
            Log.Information("GH-5545: {Project} does not recreate {Service} mid-run: this run did not start it fresh",
                projectName, service);
        }

        if (owned.Length == 0) return;

        foreach (var service in owned) watchForHealing(service);

        var unconfirmed = 0;
        supervisor.OnDegraded = async (degradation, ct) =>
        {
            var healed = new List<HealedTrack>();
            var declined = new List<string>();

            foreach (var service in owned)
            {
                var decision = await healer.HealIfDegraded(service, degradation.Reason, afterRecreate(service), ct);
                switch (decision.Action)
                {
                    case HealAction.Heal:
                        healed.Add(new HealedTrack(service, decision.Heal));
                        break;

                    case HealAction.Abort:
                        Log.Error("GH-5545: {Project} is aborted: {Reason}", projectName, decision.Reason);
                        return DegradationResponse.Abort(decision.Reason);

                    default:
                        declined.Add(decision.Reason);
                        break;
                }
            }

            if (healed.Count == 0)
            {
                Log.Warning("GH-5545: {Project} degraded, but nothing was recreated: {Reasons}", projectName,
                    string.Join("; ", declined));
                stopPausingAfterUnconfirmed(supervisor, projectName, ref unconfirmed);
            }

            return DegradationResponse.HealedTracks(healed);
        };
    }

    /// <summary>
    /// LocalGate: Bobcat's gate heals a track, so this checks the services' own probes before letting it. Without a
    /// confirmation the unit resumes and nothing is recreated.
    /// </summary>
    void confirmBeforeHealing(Supervisor supervisor, string unitName, string[] services)
    {
        if (services.Length == 0 || supervisor.OnDegraded is not { } gateHeal) return;

        var unconfirmed = 0;
        supervisor.OnDegraded = async (degradation, ct) =>
        {
            var verdicts = new List<ServiceVerdict>();
            foreach (var service in services)
            {
                var verdict = await healer.Confirm(service, ct);
                if (verdict.Degraded)
                {
                    Log.Warning("GH-5545: {Unit}: {Evidence}", unitName, verdict.Evidence);
                    return await gateHeal(degradation, ct);
                }

                verdicts.Add(verdict);
            }

            Log.Warning("GH-5545: {Unit} degraded, but its services' probes do not confirm it, so nothing is recreated: {Evidence}",
                unitName, string.Join("; ", verdicts.Select(v => v.Evidence)));
            stopPausingAfterUnconfirmed(supervisor, unitName, ref unconfirmed);
            return DegradationResponse.HealedTracks();
        };
    }

    static void stopPausingAfterUnconfirmed(Supervisor supervisor, string name, ref int unconfirmed)
    {
        if (++unconfirmed < ServiceHealer.MaxUnconfirmedPerRun) return;

        // Bobcat reads OnDegraded at each signal, so the next one is only reported, as on a run with no heal at all.
        supervisor.OnDegraded = null;
        Log.Warning("GH-5545: {Name} degraded {Count} times without its services' probes confirming it; from here its " +
                    "degradations are only reported", name, unconfirmed);
    }

    /// <summary>See <see cref="GateHealing.HealsSqlServer"/>.</summary>
    static bool gateHealsSqlServer((string Name, ServerUse SqlServer)[] suites, out string whyNot)
        => GateHealing.HealsSqlServer(
            suites.Select(s => (s.Name, s.SqlServer != ServerUse.None, s.SqlServer == ServerUse.Shared)).ToArray(), out whyNot);

    /// <summary>The services the healable tracks among <paramref name="tracks"/> recreate.</summary>
    static string[] healedServicesOf(GateDeclaration gate, string[] tracks)
        => gate.Tracks
            .Where(t => t.CanHeal && tracks.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            .Select(t => GateHealing.ServiceHealedBy(t.Name))
            .Where(x => x is not null)
            .ToArray();

    /// <summary>
    /// Gives the sqlserver and asb-emulator tracks their heal, when this gate started the service fresh (and, for SQL
    /// Server, when no two units use it at once). Called after the fresh start and before the gate runs.
    /// </summary>
    void healGateTracks(GateDeclaration gate, GateSuite[] suites, bool healSqlServer)
    {
        foreach (var track in gate.Tracks)
        {
            var service = GateHealing.ServiceHealedBy(track.Name);
            if (service is null || (service == "sqlserver" && !healSqlServer)) continue;

            if (!HealPolicy.Owns(service))
            {
                Log.Information("GH-5545: track {Track} only reports a degradation: this gate did not start {Service} fresh",
                    track.Name, service);
                continue;
            }

            watchForHealing(service);
            track.MaxHeals = ServiceHealPolicy.MaxHealsPerService;
            track.Heal((context, ct) => healer.HealNow(service, context.Degradation.Reason,
                healer.WatchOf(service)?.Verdict().Evidence,
                service == "sqlserver"
                    ? token => Task.Run(() => restoreGateSqlServerDatabases(suites, context.Unit), token)
                    : null,
                ct));
        }
    }

    /// <summary>
    /// What a recreated SQL Server has lost that the unit's workers connect to: SqlServerTests' lane databases, or a
    /// suite's own database. The default database (master) of a Shared suite is always there.
    /// </summary>
    void restoreGateSqlServerDatabases(GateSuite[] suites, GateUnit unit)
    {
        var suite = suites.Single(s => s.Name == unit.Project.Name);
        switch (suite.SqlServer)
        {
            case ServerUse.Lanes:
                Log.Warning("GH-5545: creating the lane databases wolverine_w0..{Last} again", unit.Workers - 1);
                ensureSqlServerLaneDatabases(unit.Workers);
                break;

            case ServerUse.OwnDatabase:
                Log.Warning("GH-5545: creating {Database} again", gateDatabaseName(suite));
                recreateSqlServerDatabase(gateDatabaseName(suite));
                break;

            case ServerUse.DatabasePerLane:
                for (var lane = 0; lane < unit.Workers; lane++)
                {
                    var database = laneDatabaseName(gateDatabaseName(suite), lane);
                    Log.Warning("GH-5545: creating {Database} again", database);
                    recreateSqlServerDatabase(database);
                }
                break;
        }
    }
}

