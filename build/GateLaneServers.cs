using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nuke.Common.Tooling;
using Serilog;

// GH-5958. A database SERVER of its own for each lane of a LocalGate unit, started by the gate for the run and
// removed after it.
//
// Why a server and not a database: MartenTests, PostgresqlTests, PersistenceTests and the EF suites create
// sibling databases beside their own under fixed, server-wide names (tenant1-4, db1-3, players, things,
// database1-4, ...), and several of them share those names, so they held the one `postgresql` track and ran
// one after another for ~100 minutes at the end of every full gate. Renaming every sibling per lane would touch
// ~40 call sites and every assertion that reads a database name back out of an agent URI. A server per lane makes
// every one of those names the lane's own with no test change at all, and a Postgres container costs a couple of
// seconds and a few hundred MB.
//
// Bobcat launches discovery, isolated and recycled workers as lane 0 and never while the pool is running
// (WorkerLaunchContext), so lane 0's server is never shared by two processes at once.
//
// GH-5958, broker lanes: the same for a broker a suite reaches through an address its workers are told
// (GateSuite.LaneBrokers). The Pub/Sub emulator: each lane's topics and subscriptions are on its own emulator, so
// the suite's fixed names ("orders", "sr", "colors", ...) cannot cross lanes.
//
// The containers are this run's alone: named after the compose project, the unit and the lane, published on a
// port Docker picks (so two worktrees' gates never collide on a port), recreated empty at the start of the run
// and removed at the end of it, red or green.
partial class Build
{
    /// <summary>The image docker-compose.yml's postgresql service runs, so a lane server is the same Postgres.</summary>
    const string LaneServerPostgresImage = "postgres:17";

    /// <summary>The image docker-compose.yml's gcp-pubsub service runs.</summary>
    const string LaneServerPubsubImage = "gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators";

    /// <summary>The kinds of lane server the gate can start.</summary>
    const string PostgresLaneServer = "postgres";
    const string PubsubLaneServer = "pubsub";

    /// <summary>One lane's server: the container, and the whole connection string its workers get.</summary>
    sealed record GateLaneServer(string Kind, string Container, int Lane)
    {
        public int Port { get; set; }

        public string ConnectionString => Kind switch
        {
            // As the compose default, but on the lane server's port. Database=postgres, so Servers.PostgresDatabaseName
            // and every assertion built on it read exactly what they read against the compose server.
            PostgresLaneServer => $"Host=localhost;Port={Port};Database=postgres;Username=postgres;password=postgres",

            // PUBSUB_EMULATOR_HOST's form
            PubsubLaneServer => $"localhost:{Port}",
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }

    /// <summary>The key a unit's lane servers of one kind are filed under.</summary>
    static string laneServerKey(string unit, string kind) => $"{unit}|{kind}";

    /// <summary>
    /// The lane servers of every unit whose suite asks for them, by unit name. Containers are started here and
    /// not awaited: <see cref="awaitLaneServers"/> waits on them after the build, as the compose services are.
    /// </summary>
    Dictionary<string, GateLaneServer[]> startLaneServers(IEnumerable<(string Unit, int Workers, string Kind)> units)
    {
        removeLaneServers(); // a previous run killed before its cleanup

        var servers = new Dictionary<string, GateLaneServer[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (unit, workers, kind) in units)
        {
            servers[laneServerKey(unit, kind)] = Enumerable.Range(0, workers)
                .Select(lane => new GateLaneServer(kind, laneServerContainerName(unit, kind, lane), lane))
                .ToArray();
        }

        var all = servers.Values.SelectMany(x => x).ToArray();
        if (all.Length == 0) return servers;

        Log.Information("GH-5958: starting {Count} lane server(s): {Containers}", all.Length,
            string.Join(" ", all.Select(s => s.Container)));

        Parallel.ForEach(all, server =>
        {
            var label = $"--label {LaneServerLabel}={ComposeProjectName}";
            switch (server.Kind)
            {
                case PostgresLaneServer:
                    // max_connections as the compose service has it (#2810). fsync and friends off: a lane server is
                    // thrown away at the end of the run, so durability across a crash buys nothing and costs every
                    // commit a flush.
                    docker($"run -d --name {server.Container} {label} " +
                           "--shm-size 512m -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=postgres -p 5432 " +
                           $"{LaneServerPostgresImage} postgres -c max_connections=500 -c fsync=off " +
                           "-c synchronous_commit=off -c full_page_writes=off");
                    server.Port = laneServerPort(server.Container, 5432);
                    break;

                case PubsubLaneServer:
                    // As the compose service starts it, less its debug logging
                    docker($"run -d --name {server.Container} {label} -p 8085 {LaneServerPubsubImage} " +
                           "gcloud --quiet beta emulators pubsub start --host-port 0.0.0.0:8085 --project wolverine");
                    server.Port = laneServerPort(server.Container, 8085);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(server.Kind), server.Kind, null);
            }
        });

        return servers;
    }

    const string LaneServerLabel = "wolverine.gate.lane-server";

    /// <summary>Blocks until every lane server accepts a connection and a query.</summary>
    static void awaitLaneServers(Dictionary<string, GateLaneServer[]> servers)
    {
        var all = servers.Values.SelectMany(x => x).ToArray();
        if (all.Length == 0) return;

        var clock = Stopwatch.StartNew();
        Parallel.ForEach(all, server =>
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try
                {
                    if (server.Kind == PubsubLaneServer)
                    {
                        // The emulator answers "Ok" on its root once it serves the API
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        http.GetStringAsync($"http://{server.ConnectionString}/").GetAwaiter().GetResult();
                        return;
                    }

                    using var conn = new Npgsql.NpgsqlConnection(server.ConnectionString + ";Pooling=false;Timeout=5");
                    conn.Open();
                    using var command = conn.CreateCommand();
                    command.CommandText = "select 1";
                    command.ExecuteScalar();
                    return;
                }
                catch (Exception e) when (DateTime.UtcNow < deadline)
                {
                    // The image's entrypoint starts Postgres once to initialize, stops it, then starts it for real:
                    // a connection in that window is refused or dropped
                    Log.Debug("Lane server {Container} is not ready yet: {Message}", server.Container, e.Message);
                    Thread.Sleep(500);
                }
            }
        });

        Log.Information("GH-5958: {Count} lane server(s) ready in {Seconds:0.0}s", all.Length, clock.Elapsed.TotalSeconds);
    }

    /// <summary>Removes every lane server this compose project's gate started. Never anyone else's.</summary>
    static void removeLaneServers()
    {
        var list = ProcessTasks.StartProcess("docker",
                $"ps -aq --filter label={LaneServerLabel}={ComposeProjectName}", logOutput: false, logInvocation: false)
            .AssertWaitForExit();
        var ids = list.Output.Select(x => x.Text.Trim()).Where(x => x.Length > 0).ToArray();
        if (ids.Length == 0) return;

        Log.Information("GH-5958: removing {Count} lane server(s)", ids.Length);
        docker("rm -f -v " + string.Join(" ", ids));
    }

    string laneServerContainerName(string unit, string kind, int lane)
        => $"{ComposeProjectName}-gate-{new string(unit.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray())}-{kind}-w{lane}";

    static int laneServerPort(string container, int containerPort)
    {
        // "0.0.0.0:55012" and "[::]:55012", one per line
        var output = ProcessTasks.StartProcess("docker", $"port {container} {containerPort}/tcp", logOutput: false,
            logInvocation: false).AssertZeroExitCode().Output;

        foreach (var line in output.Select(x => x.Text.Trim()))
        {
            var colon = line.LastIndexOf(':');
            if (colon > 0 && int.TryParse(line[(colon + 1)..], out var port)) return port;
        }

        throw new InvalidOperationException($"Could not read the host port Docker published for {container}:{containerPort}.");
    }

    static void docker(string args)
        => ProcessTasks.StartProcess("docker", args, logOutput: false, logInvocation: false).AssertZeroExitCode();
}
