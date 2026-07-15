using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    /// <summary>
    /// Starts specific docker compose services and waits for them to be ready.
    /// </summary>
    void StartDockerServices(params string[] services)
    {
        bool IsToolAvailable(string toolName)
        {
            try
            { ToolPathResolver.GetPathExecutable(toolName);
              return true; }
            catch (ArgumentException)
            { return false; }
        }

        string toolName = new List<string> { "docker", "podman" }
                              .FirstOrDefault(IsToolAvailable) ?? "docker";

        var serviceList = string.Join(" ", services);
        Log.Information("Starting docker services: {Services}", serviceList);

        // Pass each service as a separate argument to avoid quoting issues
        var args = "compose up -d " + serviceList;
        ProcessTasks
            .StartProcess(toolName, args, logOutput: false, logInvocation: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();

        // Wait for databases that were requested
        if (services.Contains("postgresql"))
            WaitForDatabaseToBeReady();
        if (services.Contains("sqlserver"))
            WaitForSqlServerToBeReady();
        if (services.Contains("mysql"))
            WaitForMySqlToBeReady();
        if (services.Contains("oracle"))
            WaitForOracleToBeReady();
        if (services.Contains("localstack"))
            WaitForLocalStackToBeReady();
        if (services.Contains("asb-emulator"))
            WaitForAzureServiceBusEmulatorToBeReady();
        if (services.Contains("kafka"))
            WaitForKafkaToBeReady();
        if (services.Contains("gcp-pubsub"))
            WaitForPubsubEmulatorToBeReady();
    }

    void WaitForPubsubEmulatorToBeReady()
    {
        var attempt = 0;
        while (attempt < 30)
        {
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                tcpClient.Connect("localhost", 8085);
                Log.Information("GCP Pub/Sub emulator is up and ready!");
                return;
            }
            catch (Exception)
            {
                // ignore connection errors
            }

            Thread.Sleep(2000);
            attempt++;
        }

        Log.Warning("GCP Pub/Sub emulator did not become ready after 60 seconds");
    }

    void WaitForKafkaToBeReady()
    {
        var attempt = 0;
        while (attempt < 30)
        {
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                tcpClient.Connect("localhost", 9092);
                Log.Information("Kafka is up and ready!");
                return;
            }
            catch (Exception)
            {
                // ignore connection errors
            }

            Thread.Sleep(2000);
            attempt++;
        }

        Log.Warning("Kafka did not become ready after 60 seconds");
    }

    void WaitForSqlServerToBeReady()
    {
        var attempt = 0;
        while (attempt < 30)
        {
            try
            {
                using var conn = new Microsoft.Data.SqlClient.SqlConnection("Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;Encrypt=False");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.ExecuteNonQuery();
                Log.Information("SQL Server is up and ready!");
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(2000);
                attempt++;
            }
        }

        Log.Warning("SQL Server did not become ready after 60 seconds");
    }

    void WaitForMySqlToBeReady()
    {
        var attempt = 0;
        while (attempt < 30)
        {
            try
            {
                using var conn = new MySqlConnector.MySqlConnection("Server=localhost;Port=3306;Database=wolverine;User=root;Password=P@55w0rd;");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                cmd.ExecuteNonQuery();
                Log.Information("MySQL is up and ready!");
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(2000);
                attempt++;
            }
        }

        Log.Warning("MySQL did not become ready after 60 seconds");
    }

    void WaitForOracleToBeReady()
    {
        var attempt = 0;
        while (attempt < 60)
        {
            try
            {
                using var conn = new Oracle.ManagedDataAccess.Client.OracleConnection("User Id=wolverine;Password=wolverine;Data Source=localhost:1521/FREEPDB1");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM DUAL";
                cmd.ExecuteNonQuery();
                Log.Information("Oracle is up and ready!");
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(2000);
                attempt++;
            }
        }

        Log.Warning("Oracle did not become ready after 120 seconds");
    }

    void WaitForLocalStackToBeReady()
    {
        var attempt = 0;
        using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        while (attempt < 30)
        {
            try
            {
                var response = httpClient.GetAsync("http://localhost:4566/_localstack/health").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    Log.Information("LocalStack is up and ready!");
                    return;
                }
            }
            catch (Exception)
            {
                // ignore connection errors
            }

            Thread.Sleep(2000);
            attempt++;
        }

        Log.Warning("LocalStack did not become ready after 60 seconds");
    }

    void WaitForAzureServiceBusEmulatorToBeReady()
    {
        var attempt = 0;
        while (attempt < 30)
        {
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                tcpClient.Connect("localhost", 5673);
                Log.Information("Azure Service Bus emulator is up and ready!");
                return;
            }
            catch (Exception)
            {
                // ignore connection errors
            }

            Thread.Sleep(2000);
            attempt++;
        }

        Log.Warning("Azure Service Bus emulator did not become ready after 60 seconds");
    }

    /// <summary>
    /// Builds specific test projects (not the entire solution).
    /// </summary>
    void BuildTestProjects(params AbsolutePath[] projects)
    {
        BuildTestProjectsWithFramework(null, projects);
    }

    void BuildTestProjectsWithFramework(string frameworkOverride, params AbsolutePath[] projects)
    {
        var framework = frameworkOverride ?? Framework;
        foreach (var project in projects)
        {
            Log.Information("Building {Project} ({Framework})...", project.Name, framework ?? "all");
            DotNetBuild(c => c
                .SetProjectFile(project)
                .SetConfiguration(Configuration)
                .SetFramework(framework));
        }
    }

    // ─── Persistence CI Targets ────────────────────────────────────────

    Target CIPersistence => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var persistenceTests = RootDirectory / "src" / "Persistence" / "PersistenceTests" / "PersistenceTests.csproj";
            var postgresqlTests = RootDirectory / "src" / "Persistence" / "PostgresqlTests" / "PostgresqlTests.csproj";

            BuildTestProjects(postgresqlTests);
            // Pin PersistenceTests to net9.0 in CI; csproj targets net9.0;net10.0.
            BuildTestProjectsWithFramework("net9.0", persistenceTests);
            StartDockerServices("postgresql", "sqlserver", "rabbitmq");

            RunTestProject(persistenceTests, frameworkOverride: "net9.0");
            RunTestProject(postgresqlTests);
        });

    Target CISqlite => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var sqliteTests = RootDirectory / "src" / "Persistence" / "SqliteTests" / "SqliteTests.csproj";

            BuildTestProjects(sqliteTests);

            RunTestProject(sqliteTests);
        });

    Target CISqlServer => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var sqlServerTests = RootDirectory / "src" / "Persistence" / "SqlServerTests" / "SqlServerTests.csproj";

            BuildTestProjects(sqlServerTests);
            StartDockerServices("sqlserver");

            RunTestProject(sqlServerTests);
        });

    Target CIMarten => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var martenTests = RootDirectory / "src" / "Persistence" / "MartenTests" / "MartenTests.csproj";
            var martenSubscriptionTests = RootDirectory / "src" / "Persistence" / "MartenSubscriptionTests" / "MartenSubscriptionTests.csproj";

            BuildTestProjects(martenTests, martenSubscriptionTests);
            StartDockerServices("postgresql");

            RunTestProjects([martenTests, martenSubscriptionTests]);
        });

    Target CIMySql => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var mySqlTests = RootDirectory / "src" / "Persistence" / "MySql" / "MySqlTests" / "MySqlTests.csproj";

            BuildTestProjects(mySqlTests);
            StartDockerServices("mysql");

            RunTestProject(mySqlTests);
        });

    Target CIOracle => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var oracleTests = RootDirectory / "src" / "Persistence" / "Oracle" / "OracleTests" / "OracleTests.csproj";

            BuildTestProjects(oracleTests);
            StartDockerServices("oracle");

            RunTestProject(oracleTests);
        });

    Target CIEfCore => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var efCoreTests = RootDirectory / "src" / "Persistence" / "EfCoreTests" / "EfCoreTests.csproj";
            var efCoreMultiTenancy = RootDirectory / "src" / "Persistence" / "EfCoreTests.MultiTenancy" / "EfCoreTests.MultiTenancy.csproj";

            BuildTestProjects(efCoreTests, efCoreMultiTenancy);
            // RabbitMQ is required by Bug_2588_ef_core_durable_outbox_with_conventional_routing,
            // which exercises EF Core + RabbitMQ conventional routing + durable outbox policy.
            // See GH-2588.
            StartDockerServices("postgresql", "sqlserver", "rabbitmq");

            RunTestProjects([efCoreTests, efCoreMultiTenancy]);
        });

    // ─── Transport CI Targets ──────────────────────────────────────────

    // ─── AWS CI Targets ────────────────────────────────────────────────
    //
    // CIAWS used to run the SQS project and then the SNS project back to back in a single job, which chronically
    // blew past the 20 minute job timeout on CI runners. It is now split three ways so the shards run as parallel
    // jobs. Measured locally (Release, net9.0, serial, LocalStack in docker):
    //
    //   whole SQS project  199 tests  6m27s   <- the real hog
    //   whole SNS project  118 tests  1m30s
    //
    // Within SQS, the wall-clock is concentrated in a handful of end-to-end classes (send_and_receive alone is
    // ~121s for 2 tests, end_to_end_with_named_broker ~60s for 1), while the four *SendingAndReceivingCompliance
    // batteries are ~93 tests / ~77s. Peeling the compliance batteries off into their own shard splits the project
    // into ~191s + ~77s of test time. CIAWS is retained as an aggregate so "./build.sh CIAWS" still runs everything.
    // See #3350.

    // The four batteries are named Buffered/Inline/Durable/PrefixedSendingAndReceivingCompliance, so a single
    // substring selects (and its negation excludes) all of them.
    const string ComplianceBatteries = "FullyQualifiedName~SendingAndReceivingCompliance";
    const string NotComplianceBatteries = "FullyQualifiedName!~SendingAndReceivingCompliance";

    AbsolutePath AmazonSqsTests => RootDirectory / "src" / "Transports" / "AWS" / "Wolverine.AmazonSqs.Tests" / "Wolverine.AmazonSqs.Tests.csproj";
    AbsolutePath AmazonSnsTests => RootDirectory / "src" / "Transports" / "AWS" / "Wolverine.AmazonSns.Tests" / "Wolverine.AmazonSns.Tests.csproj";

    void runAwsShard(AbsolutePath project, string testFilter = null)
    {
        BuildTestProjects(project);
        StartDockerServices("localstack", "postgresql");

        RunTestProject(project, testFilter: testFilter);
    }

    /// <summary>
    /// AWS shard 1: the SQS project, minus the sending/receiving compliance batteries.
    /// </summary>
    Target CIAWSSqs => _ => _
        .ProceedAfterFailure()
        .Executes(() => runAwsShard(AmazonSqsTests, NotComplianceBatteries));

    /// <summary>
    /// AWS shard 2: the SQS Buffered/Inline/Durable/Prefixed sending and receiving compliance batteries.
    /// </summary>
    Target CIAWSSqsCompliance => _ => _
        .ProceedAfterFailure()
        .Executes(() => runAwsShard(AmazonSqsTests, ComplianceBatteries));

    /// <summary>
    /// AWS shard 3: the whole SNS project, which is small enough not to need splitting.
    /// </summary>
    Target CIAWSSns => _ => _
        .ProceedAfterFailure()
        .Executes(() => runAwsShard(AmazonSnsTests));

    Target CIAWS => _ => _
        .ProceedAfterFailure()
        .DependsOn(CIAWSSqs, CIAWSSqsCompliance, CIAWSSns);

    Target CIKafka => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Kafka" / "Wolverine.Kafka.Tests" / "Wolverine.Kafka.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("kafka", "postgresql");

            RunTestProject(tests);
        });

    Target CIMQTT => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "MQTT" / "Wolverine.MQTT.Tests" / "Wolverine.MQTT.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql", "sqlserver");

            RunTestProject(tests);
        });

    Target CINATS => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "NATS" / "Wolverine.Nats.Tests" / "Wolverine.Nats.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            RunTestProject(tests);
        });

    Target CIPubsub => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "GCP" / "Wolverine.Pubsub.Tests" / "Wolverine.Pubsub.Tests.csproj";

            BuildTestProjects(tests);
            // Durable compliance fixtures persist through Marten/Postgres; the rest use the
            // Pub/Sub emulator started via docker compose. See #3191.
            StartDockerServices("gcp-pubsub", "postgresql");

            RunTestProject(tests);
        });

    Target CIPulsar => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Pulsar" / "Wolverine.Pulsar.Tests" / "Wolverine.Pulsar.Tests.csproj";

            BuildTestProjects(tests);

            RunTestProject(tests);
        });

    Target CIRedis => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Redis" / "Wolverine.Redis.Tests" / "Wolverine.Redis.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            RunTestProject(tests);
        });

    Target CIHttp => _ => _
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Http" / "Wolverine.Http.Tests" / "Wolverine.Http.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            var framework = Framework;
            DotNetTest(c => c
                .SetProjectFile(tests)
                .SetConfiguration(Configuration)
                .SetFramework(framework)
                .EnableNoBuild());
        });

    Target CIHttpAspVersioning => _ => _
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Http" / "Wolverine.Http.AspVersioning.Tests" / "Wolverine.Http.AspVersioning.Tests.csproj";

            BuildTestProjectsWithFramework("net10.0", tests);

            RunTestProject(tests, frameworkOverride: "net10.0");
        });

    Target CIRabbitMQ => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var rabbitTests = RootDirectory / "src" / "Transports" / "RabbitMQ" / "Wolverine.RabbitMQ.Tests" / "Wolverine.RabbitMQ.Tests.csproj";

            BuildTestProjects(rabbitTests);
            StartDockerServices("rabbitmq", "postgresql", "sqlserver");

            RunTestProject(rabbitTests);
        });

    Target CICircuitBreaking => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var circuitTests = RootDirectory / "src" / "Transports" / "RabbitMQ" / "CircuitBreakingTests" / "CircuitBreakingTests.csproj";

            BuildTestProjects(circuitTests);
            StartDockerServices("rabbitmq", "postgresql");

            RunTestProject(circuitTests);
        });

    /// <summary>
    /// MessageRoutingTests live in src/Testing but exercise core routing precedence
    /// against a real RabbitMQ broker (the convention surface that matters in
    /// production). The PR #2596 PreregisterSenders regression that motivated this
    /// target is not catchable from CoreTests — it only manifests when conventional
    /// routing actually creates broker endpoints. Keep this in CI going forward so
    /// any future refactor that breaks routing precedence between Explicit /
    /// LocalRouting / MessageRoutingConventions fails fast on PRs.
    /// </summary>
    Target CIMessageRouting => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Testing" / "MessageRoutingTests" / "MessageRoutingTests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("rabbitmq");

            RunTestProject(tests);
        });

    Target CICosmosDb => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var cosmosDbTests = RootDirectory / "src" / "Persistence" / "CosmosDbTests" / "CosmosDbTests.csproj";
            var leaderElectionTests = RootDirectory / "src" / "Persistence" / "LeaderElection" / "CosmosDbTests.LeaderElection" / "CosmosDbTests.LeaderElection.csproj";

            BuildTestProjects(cosmosDbTests, leaderElectionTests);

            RunTestProjects([cosmosDbTests, leaderElectionTests]);
        });

    Target CIRavenDb => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var ravenDbTests = RootDirectory / "src" / "Persistence" / "RavenDbTests" / "RavenDbTests.csproj";
            var leaderElectionTests = RootDirectory / "src" / "Persistence" / "LeaderElection" / "RavenDbTests.LeaderElection" / "RavenDbTests.LeaderElection.csproj";

            BuildTestProjectsWithFramework("net9.0", ravenDbTests, leaderElectionTests);

            RunTestProjects([ravenDbTests, leaderElectionTests], frameworkOverride: "net9.0");
        });

    Target CIGrpc => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Wolverine.Grpc.Tests" / "Wolverine.Grpc.Tests.csproj";

            BuildTestProjects(tests);

            RunTestProject(tests);
        });

    // ─── AOT Smoke ──────────────────────────────────────────────────────
    //
    // Builds the Wolverine.AotSmoke project, which sets IsAotCompatible=true +
    // TrimMode=full + WarningsAsErrors for IL2026/IL2046/IL2055/IL2065/IL2067/
    // IL2070/IL2072/IL2075/IL2090/IL2091/IL2111/IL3050/IL3051. If any change
    // adds a [RequiresDynamicCode] / [RequiresUnreferencedCode] dependency to a
    // Wolverine API that the smoke exercises (Envelope, WolverineOptions,
    // DeliveryOptions, the scheduling helpers, etc.), the build fails with the
    // specific warning code and call chain pinpointing the regression.
    //
    // Also runs the smoke binary end-to-end as a sanity check that the
    // exercised surfaces don't just compile but actually work.
    //
    // Companion smoke project Wolverine.AotSmoke.Static (added in #2746
    // sub-PR I) covers the static-load contract: it boots under
    // TypeLoadMode.Static + AssertAllPreGeneratedTypesExist + a sentinel
    // IAssemblyGenerator that throws on any runtime compilation. If the
    // committed pre-gen under that project's Internal/Generated/ drifts
    // or the static-load path silently falls back to Roslyn, the smoke
    // binary exits non-zero with a clear diagnostic.
    Target CIAotSmoke => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var smoke = RootDirectory / "src" / "Testing" / "Wolverine.AotSmoke" / "Wolverine.AotSmoke.csproj";
            var staticSmoke = RootDirectory / "src" / "Testing" / "Wolverine.AotSmoke.Static" / "Wolverine.AotSmoke.Static.csproj";

            DotNet($"build {smoke} --configuration {Configuration} --framework net9.0");
            DotNet($"run --project {smoke} --no-build --configuration {Configuration} --framework net9.0");

            DotNet($"build {staticSmoke} --configuration {Configuration} --framework net9.0");
            DotNet($"run --project {staticSmoke} --no-build --configuration {Configuration} --framework net9.0");
        });

    Target CIAzureServiceBus => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Azure" / "Wolverine.AzureServiceBus.Tests" / "Wolverine.AzureServiceBus.Tests.csproj";

            BuildTestProjects(tests);
            // Postgres is needed for leader election tests
            StartDockerServices("asb-emulator", "postgresql");

            RunTestProject(tests);
        });

    // ─── Polecat CI Targets ────────────────────────────────────────────
    //
    // PolecatTests is a single project of serial (NoParallelization) SqlServer integration tests. As one job it sat
    // right at — and frequently over — the 20 minute CI job timeout, so it is sharded across three parallel jobs.
    //
    // Measured locally (Release, net10.0, serial, SqlServer in docker): the whole project is 235 tests in 8m14s,
    // but the test bodies themselves only account for ~173s of that. The other ~2/3 of the wall-clock is per-CLASS
    // fixture cost (Wolverine + Polecat host bootstrap and SqlServer schema application). The shards are therefore
    // balanced by test-CLASS count, not by test count or test duration:
    //
    //   CIPolecatWorkflow   AggregateHandlerWorkflow + Bugs                            20 classes / 82 tests
    //   CIPolecatSagas      Sagas, Distribution, Subscriptions, Dcb, Publishing        19 classes / 70 tests
    //   CIPolecat           everything else (root namespace, AncillaryStores, ...)     18 classes / 83 tests
    //
    // The shards are defined ONCE below: the two named shards list their namespaces, and CIPolecat is the
    // *negation* of both lists. A brand new namespace therefore lands in CIPolecat automatically rather than
    // being silently dropped from CI. See #3350.

    AbsolutePath PolecatTests => RootDirectory / "src" / "Persistence" / "PolecatTests" / "PolecatTests.csproj";

    static readonly string[] PolecatWorkflowNamespaces =
    [
        "PolecatTests.AggregateHandlerWorkflow",
        "PolecatTests.Bugs"
    ];

    static readonly string[] PolecatSagaNamespaces =
    [
        "PolecatTests.Sagas",
        "PolecatTests.Distribution",
        "PolecatTests.Subscriptions",
        "PolecatTests.Dcb",
        "PolecatTests.Publishing"
    ];

    // A trailing '.' keeps "PolecatTests.Sagas" from also matching a future "PolecatTests.SagasSomethingElse".
    static string includeNamespaces(params string[] namespaces)
    {
        return string.Join("|", namespaces.Select(n => $"FullyQualifiedName~{n}."));
    }

    static string excludeNamespaces(params string[] namespaces)
    {
        return string.Join("&", namespaces.Select(n => $"FullyQualifiedName!~{n}."));
    }

    void runPolecatShard(string testFilter)
    {
        BuildTestProjectsWithFramework("net10.0", PolecatTests);
        StartDockerServices("sqlserver");

        RunTestProject(PolecatTests, frameworkOverride: "net10.0", testFilter: testFilter);
    }

    /// <summary>
    /// Polecat shard 1: the aggregate handler workflow tests and the Bugs regressions (mostly aggregate handler
    /// regressions themselves).
    /// </summary>
    Target CIPolecatWorkflow => _ => _
        .ProceedAfterFailure()
        .Executes(() => runPolecatShard(includeNamespaces(PolecatWorkflowNamespaces)));

    /// <summary>
    /// Polecat shard 2: sagas, multi-node distribution, subscriptions, DCB and outbox publishing.
    /// </summary>
    Target CIPolecatSagas => _ => _
        .ProceedAfterFailure()
        .Executes(() => runPolecatShard(includeNamespaces(PolecatSagaNamespaces)));

    /// <summary>
    /// Polecat shard 3: everything not claimed by CIPolecatWorkflow or CIPolecatSagas — including any newly
    /// added namespace, which lands here by default.
    /// </summary>
    Target CIPolecat => _ => _
        .ProceedAfterFailure()
        .Executes(() => runPolecatShard(
            excludeNamespaces([..PolecatWorkflowNamespaces, ..PolecatSagaNamespaces])));
}
