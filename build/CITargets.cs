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
            var sqliteTests = RootDirectory / "src" / "Persistence" / "SqliteTests" / "SqliteTests.csproj";
            var persistenceTests = RootDirectory / "src" / "Persistence" / "PersistenceTests" / "PersistenceTests.csproj";
            var sqlServerTests = RootDirectory / "src" / "Persistence" / "SqlServerTests" / "SqlServerTests.csproj";
            var postgresqlTests = RootDirectory / "src" / "Persistence" / "PostgresqlTests" / "PostgresqlTests.csproj";

            BuildTestProjects(sqliteTests, sqlServerTests, postgresqlTests);
            // PersistenceTests only targets net8.0/net9.0
            BuildTestProjectsWithFramework("net9.0", persistenceTests);
            StartDockerServices("postgresql", "sqlserver", "rabbitmq");

            RunSingleProjectOneClassAtATime(sqliteTests);
            RunSingleProjectOneClassAtATime(persistenceTests, frameworkOverride: "net9.0");
            RunSingleProjectOneClassAtATime(sqlServerTests);
            RunSingleProjectOneClassAtATime(postgresqlTests);
        });

    Target CIMySql => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var mySqlTests = RootDirectory / "src" / "Persistence" / "MySql" / "MySqlTests" / "MySqlTests.csproj";

            BuildTestProjects(mySqlTests);
            StartDockerServices("mysql");

            RunSingleProjectOneClassAtATime(mySqlTests);
        });

    Target CIOracle => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var oracleTests = RootDirectory / "src" / "Persistence" / "Oracle" / "OracleTests" / "OracleTests.csproj";

            BuildTestProjects(oracleTests);
            StartDockerServices("oracle");

            RunSingleProjectOneClassAtATime(oracleTests);
        });

    Target CIEfCore => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var efCoreTests = RootDirectory / "src" / "Persistence" / "EfCoreTests" / "EfCoreTests.csproj";
            var efCoreMultiTenancy = RootDirectory / "src" / "Persistence" / "EfCoreTests.MultiTenancy" / "EfCoreTests.MultiTenancy.csproj";

            BuildTestProjects(efCoreTests, efCoreMultiTenancy);
            StartDockerServices("postgresql", "sqlserver");

            RunSingleProjectOneClassAtATime(efCoreTests);
            RunSingleProjectOneClassAtATime(efCoreMultiTenancy);
        });

    // ─── Transport CI Targets ──────────────────────────────────────────

    Target CIAWS => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var sqsTests = RootDirectory / "src" / "Transports" / "AWS" / "Wolverine.AmazonSqs.Tests" / "Wolverine.AmazonSqs.Tests.csproj";
            var snsTests = RootDirectory / "src" / "Transports" / "AWS" / "Wolverine.AmazonSns.Tests" / "Wolverine.AmazonSns.Tests.csproj";

            BuildTestProjects(sqsTests, snsTests);
            StartDockerServices("postgresql");

            RunSingleProjectOneClassAtATime(sqsTests);
            RunSingleProjectOneClassAtATime(snsTests);
        });

    Target CIKafka => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Kafka" / "Wolverine.Kafka.Tests" / "Wolverine.Kafka.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            RunSingleProjectOneClassAtATime(tests);
        });

    Target CIMQTT => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "MQTT" / "Wolverine.MQTT.Tests" / "Wolverine.MQTT.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            RunSingleProjectOneClassAtATime(tests);
        });

    Target CINATS => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "NATS" / "Wolverine.Nats.Tests" / "Wolverine.Nats.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            RunSingleProjectOneClassAtATime(tests);
        });

    Target CIPulsar => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Pulsar" / "Wolverine.Pulsar.Tests" / "Wolverine.Pulsar.Tests.csproj";

            BuildTestProjects(tests);

            RunSingleProjectOneClassAtATime(tests);
        });

    Target CIRedis => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Redis" / "Wolverine.Redis.Tests" / "Wolverine.Redis.Tests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("postgresql");

            RunSingleProjectOneClassAtATime(tests);
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

    Target CIRabbitMQ => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var rabbitTests = RootDirectory / "src" / "Transports" / "RabbitMQ" / "Wolverine.RabbitMQ.Tests" / "Wolverine.RabbitMQ.Tests.csproj";
            var circuitTests = RootDirectory / "src" / "Transports" / "RabbitMQ" / "CircuitBreakingTests" / "CircuitBreakingTests.csproj";

            BuildTestProjects(rabbitTests, circuitTests);
            StartDockerServices("rabbitmq", "postgresql", "sqlserver");

            RunSingleProjectOneClassAtATime(rabbitTests);
            RunSingleProjectOneClassAtATime(circuitTests);
        });

    Target CICosmosDb => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var cosmosDbTests = RootDirectory / "src" / "Persistence" / "CosmosDbTests" / "CosmosDbTests.csproj";
            var leaderElectionTests = RootDirectory / "src" / "Persistence" / "LeaderElection" / "CosmosDbTests.LeaderElection" / "CosmosDbTests.LeaderElection.csproj";

            BuildTestProjects(cosmosDbTests, leaderElectionTests);

            RunSingleProjectOneClassAtATime(cosmosDbTests);
            RunSingleProjectOneClassAtATime(leaderElectionTests);
        });

    Target CIAzureServiceBus => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "Azure" / "Wolverine.AzureServiceBus.Tests" / "Wolverine.AzureServiceBus.Tests.csproj";

            BuildTestProjects(tests);

            RunSingleProjectOneClassAtATime(tests);
        });

    Target CIPolecat => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var polecatTests = RootDirectory / "src" / "Persistence" / "PolecatTests" / "PolecatTests.csproj";

            BuildTestProjectsWithFramework("net10.0", polecatTests);
            StartDockerServices("sqlserver");

            RunSingleProjectOneClassAtATime(polecatTests, frameworkOverride: "net10.0");
        });
}
