using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Bobcat.Supervisor;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

// GH-5125. The local commit-and-push gate: one command that runs, on one box, what tests.yml,
// dotnet.yml, http.yml, fsharp.yml, command-line.yml and bobcat-samples.yml run as ~50 GitHub jobs.
// It is Bobcat's Gate (Bobcat.Supervisor 1.0.0, bobcat#729, docs/local-gates.md in JasperFx/bobcat):
//
//   ./build.sh LocalGate                              every suite, then the extras
//   ./build.sh LocalGateSuites                        the supervised suites alone
//   ./build.sh LocalGateSuites --gate-only Marten     only the suites whose project name contains "Marten"
//   ./build.sh LocalGate --framework all              every framework a suite declares (EF Core 9 AND 10)
//
// Release, as in CI, unless --configuration says otherwise. The framework is .nuke/parameters.json's
// net9.0, as in CI; a suite that does not target it runs on its newest.
//
// What it does, in order:
//
//   1. Starts (or reuses) every docker-compose service a selected suite needs, without waiting.
//   2. Builds ONCE, Release, while the containers boot: one `dotnet build` per framework over a
//      generated solution of the selected test projects. Not the whole solution: `-f` on a
//      solution applies to every project in it, and wolverine.slnx has net9.0-only and net10.0-only
//      projects side by side (bobcat#774), while building it unpinned builds everything twice.
//   3. Waits for the services' real readiness probes (AwaitDockerServices), then runs every suite as
//      a unit of one Bobcat gate: longest first, concurrently, under one worker budget, never two
//      units on one track. Each unit's supervisor is configured exactly as its CI target's is
//      (configureSupervisor), and each leaves the same ledger behind, filed under "LocalGate".
//   4. LocalGate then runs the gates that are not test hosts, one after another, as the targets they
//      are: the describe-handlers / event-model smoke, the AOT smoke, the Bobcat samples, then the two
//      codegen gates, which rewrite the tree.
//
// THE ISOLATION RULES, which are the whole design. A track is exclusive: units naming one run one at a
// time. What collides on a shared server is NAMES (schemas, queues, topics, sibling databases), never
// the server itself, so:
//
//   - Postgres and SQL Server: a suite that only ever touches the database its connection string
//     names gets a database of its own (wolverine_gate_<suite>, recreated per run, handed to every
//     worker as ConnectionStrings__postgres / ConnectionStrings__sqlserver, which src/Servers.cs
//     reads first) and holds no track at all. A suite that also creates databases BESIDE it under
//     fixed names (tenant1-4, db1-3, players, things, ...) runs exactly as CI runs it, against the
//     default database, and holds the `postgresql` / `sqlserver` track: those names are server-wide,
//     and MartenTests, PostgresqlTests, PersistenceTests and the EF suites share several of them.
//     SqlServerTests keeps its own four lanes (wolverine_w0..3, LaneDatabases-scoped siblings).
//   - Brokers and emulators (rabbitmq, kafka, localstack, the ASB emulator, pulsar, gcp-pubsub,
//     nats, mysql, oracle, azurite, fake-gcs-server) are one track each: queue, topic and bucket
//     names are hard-coded across their suites.
//   - Fixed local ports are tracks too: the two MQTT suites both host a broker on 1883/1884; the
//     Cosmos DB suites each start an emulator through Testcontainers, which is heavy enough that two
//     at once is a bad trade; RavenDB's embedded servers.
//   - Everything else (CoreTests, the extension suites, SQLite, Fisher, gRPC, the F# compile gates)
//     holds nothing and is limited only by the worker budget.
//
// `VerifyLocalGateCoverage` fails when a test project in wolverine.slnx is neither a suite here nor
// named in NotGated with its reason, so a new suite cannot silently stay out of the gate.
// `LocalGateManifest` writes build/gates.json from the same table, for Stoat and anything else that
// wants the list without compiling the build.
partial class Build
{
    [Parameter("LocalGate: worker processes across every unit at once (default: half the cores)")]
    readonly int? GateWorkers;

    [Parameter("LocalGate: run only the suites whose project name contains one of these (case-insensitive)")]
    readonly string[] GateOnly;

    [Parameter("LocalGate: reuse the test hosts already built under bin/Release instead of building them")]
    readonly bool GateSkipBuild;

    const string GateLedgerJob = "LocalGate";

    /// <summary>
    /// How a suite uses one of the two relational servers. See the isolation rules at the top.
    /// </summary>
    enum ServerUse
    {
        None,

        /// <summary>A database of its own, recreated per run; holds no track.</summary>
        OwnDatabase,

        /// <summary>The default database, as CI runs it, holding the server's track.</summary>
        Shared,

        /// <summary>SqlServerTests: its own wolverine_w0..N-1 lanes, as CISqlServer runs it.</summary>
        Lanes
    }

    /// <summary>One test project of the gate and everything its CI target knows about running it.</summary>
    sealed record GateSuite(string Path, string CITargets)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

        /// <summary>Docker-compose services the suite needs, besides postgresql and sqlserver.</summary>
        public string[] Services { get; init; } = [];

        /// <summary>Exclusive resources that are not compose services (fixed ports, heavy emulators).</summary>
        public string[] Holds { get; init; } = [];

        public ServerUse Postgres { get; init; }
        public ServerUse SqlServer { get; init; }
        public int Workers { get; init; } = 1;

        /// <summary>Frameworks the gate may pick from, when CI pins the suite to fewer than it declares.</summary>
        public string[] Frameworks { get; init; }

        /// <summary>Extra environment for every worker.</summary>
        public Dictionary<string, string> Environment { get; init; } = new();

        public IEnumerable<string> ComposeServices()
        {
            if (Postgres != ServerUse.None) yield return "postgresql";
            if (SqlServer != ServerUse.None) yield return "sqlserver";
            foreach (var service in Services) yield return service;
        }

        public IEnumerable<string> Tracks()
        {
            if (Postgres == ServerUse.Shared) yield return "postgresql";
            if (SqlServer == ServerUse.Shared) yield return "sqlserver";
            foreach (var service in Services) yield return service;
            foreach (var resource in Holds) yield return resource;
        }
    }

    /// <summary>
    /// Every suite the CI workflows run, one entry per test project. Each project's CI shards (CIMarten,
    /// CIMartenDistribution, CIMartenTenancy, ...) fold back into one unit here: the shards exist to fit
    /// GitHub's 20 minute job cap, and the namespaces they split by are the whole project between them.
    /// </summary>
    static readonly GateSuite[] GateSuites =
    [
        // ─── dotnet.yml (`./build.sh ci`) ───
        new("src/Testing/CoreTests/CoreTests.csproj", "CoreTests"),
        new("src/Testing/MessageRoutingTests/MessageRoutingTests.csproj", "CIMessageRouting") { Services = ["rabbitmq"] },

        // ─── Persistence ───
        new("src/Persistence/PersistenceTests/PersistenceTests.csproj", "CIPersistence")
        {
            Postgres = ServerUse.Shared, SqlServer = ServerUse.Shared, Services = ["rabbitmq"], Frameworks = ["net9.0"]
        },
        new("src/Persistence/PostgresqlTests/PostgresqlTests.csproj", "CIPersistence") { Postgres = ServerUse.Shared },
        new("src/Persistence/MartenTests/MartenTests.csproj", "CIMarten, CIMartenDistribution, CIMartenTenancy")
        {
            Postgres = ServerUse.Shared
        },
        new("src/Persistence/MartenSubscriptionTests/MartenSubscriptionTests.csproj", "CIMarten") { Postgres = ServerUse.OwnDatabase },
        new("src/Persistence/EfCoreTests/EfCoreTests.csproj", "CIEfCore, CIEfCoreNet10")
        {
            Postgres = ServerUse.Shared, SqlServer = ServerUse.Shared, Services = ["rabbitmq"]
        },
        new("src/Persistence/EfCoreTests.MultiTenancy/EfCoreTests.MultiTenancy.csproj", "CIEfCoreNet10")
        {
            Postgres = ServerUse.Shared, SqlServer = ServerUse.Shared, Services = ["rabbitmq"]
        },
        new("src/Persistence/SqlServerTests/SqlServerTests.csproj", "CISqlServer, CISqlServerTransport")
        {
            SqlServer = ServerUse.Lanes, Workers = 4
        },
        new("src/Persistence/PolecatTests/PolecatTests.csproj", "CIPolecat, CIPolecatWorkflow, CIPolecatSagas")
        {
            SqlServer = ServerUse.Shared, Frameworks = ["net10.0"]
        },
        new("src/Persistence/Polecat/PolecatIncidentService.Tests/PolecatIncidentService.Tests.csproj", "CIPolecat")
        {
            SqlServer = ServerUse.Shared, Frameworks = ["net10.0"]
        },
        new("src/Persistence/MySql/MySqlTests/MySqlTests.csproj", "CIMySql") { Services = ["mysql"] },
        new("src/Persistence/Oracle/OracleTests/OracleTests.csproj", "CIOracle") { Services = ["oracle"] },
        new("src/Persistence/SqliteTests/SqliteTests.csproj", "CISqlite"),
        new("src/Persistence/FisherTests/FisherTests.csproj", "CIFisher"),
        new("src/Persistence/InMemoryTests/InMemoryTests.csproj", "CIFisher"),
        new("src/Persistence/CosmosDbTests/CosmosDbTests.csproj", "CICosmosDb") { Holds = ["cosmos-emulator"] },
        new("src/Persistence/LeaderElection/CosmosDbTests.LeaderElection/CosmosDbTests.LeaderElection.csproj", "CICosmosDb")
        {
            Holds = ["cosmos-emulator"]
        },
        new("src/Persistence/RavenDbTests/RavenDbTests.csproj", "CIRavenDb") { Holds = ["ravendb"], Frameworks = ["net9.0"] },
        new("src/Persistence/LeaderElection/RavenDbTests.LeaderElection/RavenDbTests.LeaderElection.csproj", "CIRavenDb")
        {
            Holds = ["ravendb"], Frameworks = ["net9.0"]
        },
        new("src/Persistence/Wolverine.ClaimCheck.Postgresql.Tests/Wolverine.ClaimCheck.Postgresql.Tests.csproj", "CIClaimCheck")
        {
            Postgres = ServerUse.OwnDatabase
        },
        new("src/Persistence/Wolverine.ClaimCheck.SqlServer.Tests/Wolverine.ClaimCheck.SqlServer.Tests.csproj", "CIClaimCheck")
        {
            SqlServer = ServerUse.OwnDatabase
        },
        new("src/Persistence/Wolverine.ClaimCheck.Marten.Tests/Wolverine.ClaimCheck.Marten.Tests.csproj", "CIClaimCheck")
        {
            Postgres = ServerUse.OwnDatabase
        },
        new("src/Persistence/Wolverine.ClaimCheck.GoogleCloudStorage.Tests/Wolverine.ClaimCheck.GoogleCloudStorage.Tests.csproj", "CIClaimCheck")
        {
            Services = ["fake-gcs-server"]
        },
        new("src/Persistence/Wolverine.ClaimCheck.Nats.Tests/Wolverine.ClaimCheck.Nats.Tests.csproj", "CIClaimCheck") { Services = ["nats"] },
        new("src/Persistence/Wolverine.AmazonS3.Tests/Wolverine.AmazonS3.Tests.csproj", "CIAmazonS3") { Services = ["localstack"] },
        new("src/Persistence/Wolverine.AzureBlobStorage.Tests/Wolverine.AzureBlobStorage.Tests.csproj", "CIAzureBlobStorage")
        {
            Services = ["azurite"]
        },

        // ─── Transports ───
        new("src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Wolverine.RabbitMQ.Tests.csproj", "CIRabbitMQ")
        {
            Services = ["rabbitmq"], Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Transports/RabbitMQ/CircuitBreakingTests/CircuitBreakingTests.csproj", "CICircuitBreaking")
        {
            Services = ["rabbitmq"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/AWS/Wolverine.AmazonSqs.Tests/Wolverine.AmazonSqs.Tests.csproj", "CIAWSSqs, CIAWSSqsCompliance")
        {
            Services = ["localstack"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/AWS/Wolverine.AmazonSns.Tests/Wolverine.AmazonSns.Tests.csproj", "CIAWSSns")
        {
            Services = ["localstack"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Wolverine.AzureServiceBus.Tests.csproj",
            "CIAzureServiceBus, CIAzureServiceBusLeader, CIAzureServiceBusRouting")
        {
            Services = ["asb-emulator"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/Kafka/Wolverine.Kafka.Tests/Wolverine.Kafka.Tests.csproj", "CIKafka")
        {
            Services = ["kafka"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/MQTT/Wolverine.MQTT.Tests/Wolverine.MQTT.Tests.csproj", "CIMQTT")
        {
            Holds = ["mqtt-broker-ports"], Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Transports/MQTT/Wolverine.Mqtt5.Tests/Wolverine.Mqtt5.Tests.csproj", "CIMQTT5")
        {
            Holds = ["mqtt-broker-ports"], Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        // Uses the compose NATS on 4222 when it answers (it does: ClaimCheck.Nats needs it), so it
        // shares that track; CINATS itself never starts nats and the suite falls back to Testcontainers.
        new("src/Transports/NATS/Wolverine.Nats.Tests/Wolverine.Nats.Tests.csproj", "CINATS")
        {
            Services = ["nats"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/GCP/Wolverine.Pubsub.Tests/Wolverine.Pubsub.Tests.csproj", "CIPubsub")
        {
            Services = ["gcp-pubsub"], Postgres = ServerUse.OwnDatabase
        },
        new("src/Transports/Pulsar/Wolverine.Pulsar.Tests/Wolverine.Pulsar.Tests.csproj", "CIPulsar")
        {
            Services = ["pulsar"], Postgres = ServerUse.OwnDatabase,
            // As CIPulsar sets them: the fixture then uses the compose broker instead of starting one
            // container per worker process (GH-3799).
            Environment = new()
            {
                ["WOLVERINE_PULSAR"] = "pulsar://localhost:6650",
                ["WOLVERINE_PULSAR_HTTP"] = "http://localhost:8080"
            }
        },
        new("src/Transports/Redis/Wolverine.Redis.Tests/Wolverine.Redis.Tests.csproj", "CIRedis") { Postgres = ServerUse.OwnDatabase },
        new("src/Testing/OpenTelemetry/TracingTests/TracingTests.csproj", "CIOtel") { Services = ["rabbitmq"] },
        new("src/Wolverine.Grpc.Tests/Wolverine.Grpc.Tests.csproj", "CIGrpc"),

        // ─── http.yml ───
        new("src/Http/Wolverine.Http.Tests/Wolverine.Http.Tests.csproj", "CIHttp") { Postgres = ServerUse.OwnDatabase },
        new("src/Http/Wolverine.Http.AspVersioning.Tests/Wolverine.Http.AspVersioning.Tests.csproj", "CIHttpAspVersioning")
        {
            Frameworks = ["net10.0"]
        },

        // ─── CIBobcat ───
        new("src/Bobcat/Wolverine.Bobcat.Tests/Wolverine.Bobcat.Tests.csproj", "CIBobcat")
        {
            Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Bobcat/Wolverine.Bobcat.Http.Tests/Wolverine.Bobcat.Http.Tests.csproj", "CIBobcat")
        {
            Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Bobcat/Bobcat.CritterStackScenario.Tests/Bobcat.CritterStackScenario.Tests.csproj", "CIBobcat")
        {
            Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Bobcat/Wolverine.Bobcat.EventModel.Tests/Wolverine.Bobcat.EventModel.Tests.csproj", "CIBobcat")
        {
            Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Samples/IncidentService/IncidentService.Tests/IncidentService.Tests.csproj", "CIBobcat")
        {
            Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },
        new("src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers.Tests/ProcessManagerViaHandlers.Tests.csproj", "CIBobcat")
        {
            Postgres = ServerUse.OwnDatabase, SqlServer = ServerUse.OwnDatabase
        },

        // ─── CIExtensions ───
        new("src/Extensions/Wolverine.AI.Tests/Wolverine.AI.Tests.csproj", "CIExtensions"),
        new("src/Extensions/Wolverine.DataAnnotationsValidation.Tests/Wolverine.DataAnnotationsValidation.Tests.csproj", "CIExtensions"),
        new("src/Extensions/Wolverine.FluentValidation.Tests/Wolverine.FluentValidation.Tests.csproj", "CIExtensions"),
        new("src/Extensions/Wolverine.MemoryPack.Tests/Wolverine.MemoryPack.Tests.csproj", "CIExtensions"),
        new("src/Extensions/Wolverine.MessagePack.Tests/Wolverine.MessagePack.Tests.csproj", "CIExtensions"),
        new("src/Extensions/Wolverine.Protobuf.Tests/Wolverine.Protobuf.Tests.csproj", "CIExtensions"),

        // ─── fsharp.yml: `dotnet test` in that workflow, supervised here. No services in CI. ───
        new("src/Testing/Wolverine.Core.FSharpTests/Wolverine.Core.FSharpTests.csproj", "fsharp.yml"),
        new("src/Testing/Wolverine.Http.FSharpTests/Wolverine.Http.FSharpTests.csproj", "fsharp.yml"),
        new("src/Testing/Wolverine.EfCore.FSharpTests/Wolverine.EfCore.FSharpTests.csproj", "fsharp.yml"),
        new("src/Testing/Wolverine.Marten.FSharpTests/Wolverine.Marten.FSharpTests.csproj", "fsharp.yml"),
        new("src/Testing/Wolverine.MartenAggregate.FSharpTests/Wolverine.MartenAggregate.FSharpTests.csproj", "fsharp.yml"),
        new("src/Testing/Wolverine.Cosmos.FSharpTests/Wolverine.Cosmos.FSharpTests.csproj", "fsharp.yml"),
        new("src/Testing/Wolverine.Behavioural.FSharpTests/Wolverine.Behavioural.FSharpTests.csproj", "fsharp.yml"),
    ];

    /// <summary>
    /// The test projects in wolverine.slnx the gate deliberately leaves out, each with its reason. The
    /// gate is the CI workflows' set: a project no workflow runs is listed here rather than added, so
    /// the gate never goes red on a suite CI has never held to green.
    /// </summary>
    static readonly Dictionary<string, string> NotGated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wolverine.ComplianceTests"] = "a helper library of compliance suites, not a test host (IsTestingPlatformApplication=false)",
        ["SlowTests"] = "wall-clock bound by design; slow-tests.yml runs it by hand (CISlowTests), not on a push",
        ["ChaosTesting"] = "a soak, in no workflow",
        ["PostgresqlTests.LeaderElection"] = "in no workflow",
        ["SqlServerTests.LeaderElection"] = "in no workflow",
        ["MySqlTests.LeaderElection"] = "in no workflow",
        ["OracleTests.LeaderElection"] = "in no workflow",
        ["Wolverine.SignalR.Tests"] = "in no workflow",
        ["Wolverine.HealthChecks.Tests"] = "in no workflow",
        ["BackPressureTests"] = "in no workflow",
        ["MetricsTests"] = "in no workflow",
        ["PolicyTests"] = "only the Test target runs it, and no workflow runs Test",
        ["DiagnosticsTests"] = "a sample's tests, in no workflow",
        ["TeleHealth.Tests"] = "a sample's tests, in no workflow",
        ["MultiTenantedTodoWebService.Tests"] = "a sample's tests, in no workflow",
        ["ItemService.Tests"] = "a sample's tests; only the Full target runs it, and no workflow runs Full",
        ["AppWithMiddleware.Tests"] = "a sample's tests; only the Full target runs it, and no workflow runs Full",
        ["BankingService.Tests"] = "a sample's tests; only the Full target runs it, and no workflow runs Full",
        ["TodoWebServiceTests"] = "a sample's tests; only the Full target runs it, and no workflow runs Full",
    };

    /// <summary>
    /// The gates that are not test hosts, which <see cref="LocalGate"/> runs after the suites, in this
    /// order, as the ordinary targets they are. CICodegenTest wipes ConsoleApp's generated tree and
    /// CICodegenDrift regenerates every committed tree, so drift goes last and leaves the tree as the
    /// source says it should be.
    /// </summary>
    static readonly (string Target, string Workflow)[] GateExtras =
    [
        ("CICommandLineSmoke", "command-line.yml"),
        ("CIAotSmoke", "tests.yml"),
        ("BobcatSamples", "bobcat-samples.yml"),
        ("CICodegenTest", "command-line.yml"),
        ("CICodegenDrift", "command-line.yml"),
    ];

    /// <summary>
    /// The whole gate: <see cref="LocalGateSuites"/>, then the extras. Every one runs even when an
    /// earlier one is red, and Nuke's summary names each with its verdict and duration.
    /// </summary>
    Target LocalGate => _ => _
        .Description("GH-5125: every CI suite on this box through Bobcat's Gate, then the AOT, samples, command-line and codegen gates")
        .DependsOn(LocalGateSuites, CICommandLineSmoke, CIAotSmoke, BobcatSamples, CICodegenTest, CICodegenDrift);

    /// <summary>
    /// The supervised suites alone: build once, then every suite as a unit of one Bobcat gate.
    /// </summary>
    Target LocalGateSuites => _ => _
        .Description("GH-5125: every CI test suite on this box through Bobcat's Gate -- build once, longest first, tracks per Docker service")
        .ProceedAfterFailure()
        .Before(CICommandLineSmoke, CIAotSmoke, BobcatSamples, CICodegenTest, CICodegenDrift)
        .Executes(async () =>
        {
            var clock = Stopwatch.StartNew();
            verifyLocalGateCoverage();

            var suites = GateSuites
                .Where(s => GateOnly is not { Length: > 0 } ||
                            GateOnly.Any(x => s.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (suites.Length == 0)
                throw new InvalidOperationException($"--gate-only {string.Join(" ", GateOnly)} matched no suite.");

            var gate = new GateDeclaration
            {
                Frameworks = FrameworkSelection.Parse(Framework),
                Configuration = Configuration,
                SkipBuild = true, // built below, overlapped with the containers' start
                DurationsPath = GateOnly is { Length: > 0 } ? null : RootDirectory / ".bobcat" / "gate-durations.json",
                Log = message => Log.Information("{Message}", message)
            };
            if (GateWorkers is { } budget) gate.WorkerBudget = budget;

            foreach (var track in suites.SelectMany(s => s.Tracks()).Distinct(StringComparer.OrdinalIgnoreCase))
                gate.Tracks.Add(new Track(track));

            foreach (var suite in suites)
            {
                var project = gate.Project(RootDirectory / suite.Path, suite.Workers)
                    .Targeting(suite.Frameworks ?? gateFrameworksOf(suite))
                    .Supervise((unit, supervisor) => configureSupervisor(supervisor, suite.Name, unit.Framework, null,
                        message => Log.Information("  [{Unit}] {Message}", unit.Name, message)))
                    // Provisioned when the unit starts rather than before the gate: a database is the
                    // unit's alone, and recreating it is only safe once the unit is the one running.
                    .LaunchingWith(unit => workerFactoryFor(unit.Executable, gateConnectionStrings(suite, unit),
                        suite.Environment.Count > 0 ? suite.Environment : null));

                var tracks = suite.Tracks().ToArray();
                if (tracks.Length > 0) project.On(tracks);
            }

            var units = gate.Units();
            var services = suites.SelectMany(s => s.ComposeServices()).Distinct().OrderBy(x => x).ToArray();

            Log.Information("=== LocalGate: {Units} unit(s) from {Suites} suite(s), {Services} ===",
                units.Count, suites.Length, services.Length == 0 ? "no docker services" : string.Join(" ", services));

            if (services.Length > 0) LaunchDockerServices(services);
            if (!GateSkipBuild) buildGateUnits(units);
            if (services.Length > 0) AwaitDockerServices(services);

            var cleanBefore = cleanFSharpFixtures();
            var results = await gate.Run();
            restoreFSharpFixtures(cleanBefore);

            foreach (var unit in results.Units.Where(u => u.Results is not null))
                recordLedger(unit.Unit.Project.Name, unit.Unit.Framework, unit.Results, GateLedgerJob);

            clock.Stop();
            Console.WriteLine();
            Console.WriteLine($"LocalGateSuites: {humanize(clock.Elapsed)} wall clock with the build; suites {humanize(results.Duration)} " +
                              $"({humanize(results.SequentialDuration)} one after the other) on {results.WorkerBudget} worker(s), " +
                              $"exit code {results.ExitCode}");

            if (results.ExitCode != 0)
                throw new InvalidOperationException(
                    $"LocalGateSuites is red: exit code {results.ExitCode}. The report above names every unit.");
        });

    Target VerifyLocalGateCoverage => _ => _
        .Description("Fails when a test project in wolverine.slnx is neither a LocalGate suite nor in NotGated. GH-5125.")
        .Executes(verifyLocalGateCoverage);

    Target LocalGateManifest => _ => _
        .Description("Writes build/gates.json, LocalGate's suites and extras, from the table in build/LocalGate.cs. GH-5125.")
        .Executes(() =>
        {
            File.WriteAllText(GateManifestPath, gateManifestJson());
            Log.Information("Wrote {Path}", GateManifestPath);
        });

    static AbsolutePath GateManifestPath => RootDirectory / "build" / "gates.json";

    /// <summary>
    /// The frameworks a suite's project declares: its own TargetFramework(s), or the repository's
    /// net9.0;net10.0 from Directory.Build.props when it declares none, which the gate's project-file
    /// reader cannot see.
    /// </summary>
    static string[] gateFrameworksOf(GateSuite suite)
    {
        var declared = declaredFrameworks(RootDirectory / suite.Path);
        if (declared.Count > 0) return declared.ToArray();

        return TargetFrameworks.Declared(RootDirectory / "Directory.Build.props").ToArray() is { Length: > 0 } inherited
            ? inherited
            : throw new InvalidOperationException("Could not read <TargetFrameworks> from Directory.Build.props.");
    }

    /// <summary>
    /// One `dotnet build` per framework, over a generated solution of the test projects that run on it.
    /// Their references build on whatever framework each negotiates, exactly as in any other build.
    /// </summary>
    void buildGateUnits(IReadOnlyList<GateUnit> units)
    {
        var directory = RootDirectory / "artifacts" / "local-gate";
        directory.CreateDirectory();

        foreach (var group in units.GroupBy(u => u.Framework).OrderByDescending(g => g.Key, StringComparer.Ordinal))
        {
            var filter = directory / $"gate.{group.Key}.slnx";
            var projects = group
                .Select(u => Path.GetRelativePath(RootDirectory, u.Project.Path).Replace('\\', '/'))
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            // A solution of its own rather than a filter over wolverine.slnx: that marks its net10.0-only
            // projects <Build Project="false" /> (Compile builds it pinned to net9.0), and a filter
            // inherits the mark.
            File.WriteAllText(filter, "<Solution>\n" + string.Concat(projects.Select(p =>
                $"  <Project Path=\"{Path.GetRelativePath(directory, RootDirectory / p).Replace('\\', '/')}\" />\n")) + "</Solution>\n");

            Log.Information("=== LocalGate: building {Count} test project(s) for {Framework} ===", projects.Length, group.Key);
            DotNetBuild(s => s
                .SetProjectFile(filter)
                .SetConfiguration(Configuration)
                .SetFramework(group.Key)
                // Without this, a project the solution does not list (Wolverine itself, every library)
                // builds with Configuration unset, that is Debug, and its Debug assembly is what the
                // Release test host copies.
                .SetProperty("ShouldUnsetParentConfigurationAndPlatform", "false")
                // And the test projects find xunit.runner.json at $(SolutionDir), the repository root.
                .SetProperty("SolutionDir", RootDirectory.ToString().TrimEnd('/') + "/")
                .SetProcessEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1"));
        }
    }

    /// <summary>
    /// The connection strings a unit's workers get. A database of its own for each server the suite
    /// uses that way (recreated now, as the unit starts); SqlServerTests' lanes; nothing for Shared,
    /// which is the default database Servers.cs falls back to.
    /// </summary>
    WorkerConnectionStrings gateConnectionStrings(GateSuite suite, GateUnit unit)
    {
        var connectionStrings = new WorkerConnectionStrings();
        var database = "wolverine_gate_" + new string(suite.Name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

        if (suite.Postgres == ServerUse.OwnDatabase)
        {
            recreatePostgresDatabase(database);
            var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnectionString) { Database = database }
                .ConnectionString;
            connectionStrings.Add("postgres", connectionString);
        }

        if (suite.SqlServer == ServerUse.OwnDatabase)
        {
            recreateSqlServerDatabase(database);
            var connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerConnectionString)
            {
                InitialCatalog = database,
                MultipleActiveResultSets = true
            }.ConnectionString;
            connectionStrings.Add("sqlserver", connectionString);
        }

        if (suite.SqlServer == ServerUse.Lanes)
        {
            ensureSqlServerLaneDatabases(unit.Workers);
            connectionStrings.Add("sqlserver", context => sqlServerLaneConnectionString(context.Lane));
        }

        return connectionStrings.Names.Count > 0 ? connectionStrings : null;
    }

    void recreatePostgresDatabase(string database)
    {
        using var conn = new Npgsql.NpgsqlConnection(PostgresConnectionString + ";Pooling=false");
        conn.Open();

        using var command = conn.CreateCommand();
        // Identifiers, not values: generated from the project name above, never user input.
        command.CommandText = $"DROP DATABASE IF EXISTS {database} WITH (FORCE)";
        command.ExecuteNonQuery();
        command.CommandText = $"CREATE DATABASE {database}";
        command.ExecuteNonQuery();
    }

    void recreateSqlServerDatabase(string database)
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(SqlServerConnectionString);
        conn.Open();

        using var command = conn.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText =
            $"if DB_ID('{database}') is not null begin ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}] end; " +
            $"CREATE DATABASE [{database}]";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The F# compile-gate suites regenerate their fixtures' committed Generated.fs as they run, as they do
    /// in fsharp.yml's throwaway checkout. Here the checkout is yours, so a fixture that was clean before
    /// the gate is put back after it; one you had already changed is left alone.
    /// </summary>
    static string[] cleanFSharpFixtures()
    {
        var fixtures = Directory.GetDirectories(RootDirectory / "src" / "Testing", "*.FSharpFixture")
            .Select(d => Path.Combine(d, "Generated.fs"))
            .Where(File.Exists)
            .Select(f => Path.GetRelativePath(RootDirectory, f).Replace('\\', '/'))
            .ToArray();
        if (fixtures.Length == 0) return [];

        var status = ProcessTasks.StartProcess("git", "status --porcelain -- " + string.Join(" ", fixtures),
            workingDirectory: RootDirectory, logOutput: false);
        status.AssertZeroExitCode();
        var dirty = status.Output.Select(x => x.Text.Length > 3 ? x.Text[3..].Trim() : "").ToHashSet();
        return fixtures.Where(f => !dirty.Contains(f)).ToArray();
    }

    static void restoreFSharpFixtures(string[] cleanBefore)
    {
        if (cleanBefore.Length == 0) return;

        var status = ProcessTasks.StartProcess("git", "status --porcelain -- " + string.Join(" ", cleanBefore),
            workingDirectory: RootDirectory, logOutput: false);
        status.AssertZeroExitCode();
        var rewritten = status.Output.Select(x => x.Text.Length > 3 ? x.Text[3..].Trim() : "")
            .Where(f => f.Length > 0).ToArray();
        if (rewritten.Length == 0) return;

        Log.Information("Putting back the F# fixture(s) the F# suites regenerated: {Files}", string.Join(", ", rewritten));
        ProcessTasks.StartProcess("git", "checkout -- " + string.Join(" ", rewritten), workingDirectory: RootDirectory)
            .AssertZeroExitCode();
    }

    void verifyLocalGateCoverage()
    {
        var solution = File.ReadAllText(RootDirectory / "wolverine.slnx");
        var projects = System.Text.RegularExpressions.Regex.Matches(solution, @"Path=""([^""]+\.csproj)""")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Where(p => File.Exists(RootDirectory / p) && File.ReadAllText(RootDirectory / p)
                .Contains("xunit.v3", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (projects.Length == 0)
            throw new InvalidOperationException("Found no xUnit v3 project in wolverine.slnx. That is a broken guard, not a clean one.");

        var gated = GateSuites.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = projects
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(name => !gated.Contains(name) && !NotGated.ContainsKey(name))
            .OrderBy(x => x)
            .ToArray();

        var stale = GateSuites.Where(s => !File.Exists(RootDirectory / s.Path)).Select(s => s.Path).ToArray();

        if (missing.Length > 0 || stale.Length > 0)
        {
            throw new InvalidOperationException(
                (missing.Length > 0
                    ? $"Test project(s) neither in LocalGate's GateSuites nor in NotGated (build/LocalGate.cs): {string.Join(", ", missing)}. " +
                      "Add each to GateSuites with the services its CI target starts, or to NotGated with the reason it runs in no workflow. "
                    : "") +
                (stale.Length > 0 ? $"GateSuites names project file(s) that do not exist: {string.Join(", ", stale)}." : ""));
        }

        if (!File.Exists(GateManifestPath) || File.ReadAllText(GateManifestPath) != gateManifestJson())
        {
            throw new InvalidOperationException(
                $"{GateManifestPath} is out of date with build/LocalGate.cs. Run `./build.sh LocalGateManifest` and commit it.");
        }

        Log.Information("LocalGate covers {Gated} of {Total} test project(s) in wolverine.slnx; {NotGated} are in no workflow",
            projects.Length - projects.Count(p => NotGated.ContainsKey(Path.GetFileNameWithoutExtension(p))),
            projects.Length, NotGated.Count);
    }

    static string gateManifestJson()
    {
        var manifest = new
        {
            description = "Generated by ./build.sh LocalGateManifest from build/LocalGate.cs (GH-5125). Do not edit.",
            command = "./build.sh LocalGate",
            configuration = "Release",
            suites = GateSuites.Select(s => new
            {
                name = s.Name,
                path = s.Path,
                ciTargets = s.CITargets,
                workers = s.Workers,
                frameworks = s.Frameworks,
                postgres = s.Postgres == ServerUse.None ? null : s.Postgres.ToString(),
                sqlServer = s.SqlServer == ServerUse.None ? null : s.SqlServer.ToString(),
                services = s.ComposeServices().ToArray(),
                tracks = s.Tracks().ToArray()
            }),
            extras = GateExtras.Select(x => new { target = x.Target, workflow = x.Workflow }),
            notGated = NotGated.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value)
        };

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }) + "\n";
    }

    static string humanize(TimeSpan duration)
        => duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h{duration.Minutes:00}m"
            : duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s"
            : $"{duration.TotalSeconds:0}s";
}
