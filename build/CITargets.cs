using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Bobcat.Supervisor;
using Confluent.Kafka;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    /// <summary>
    /// Starts specific docker compose services and waits for them to be ready. Targets that have
    /// real work to do between the two halves (compiling the test projects, typically) should call
    /// <see cref="LaunchDockerServices"/> first and <see cref="AwaitDockerServices"/> after, so a
    /// container's boot overlaps the compile instead of following it.
    /// </summary>
    void StartDockerServices(params string[] services)
    {
        LaunchDockerServices(services);
        AwaitDockerServices(services);
    }

    /// <summary>
    /// How many times <see cref="ComposeUp"/> will try `docker compose up -d` before giving up.
    /// </summary>
    const int DockerComposeAttempts = 3;

    /// <summary>
    /// Fires `docker compose up -d` for the services and returns as soon as the containers are
    /// launched — no readiness wait. Pair with <see cref="AwaitDockerServices"/>.
    /// </summary>
    void LaunchDockerServices(params string[] services)
    {
        var serviceList = string.Join(" ", services);
        Log.Information("Starting docker services: {Services}", serviceList);

        // Pass each service as a separate argument to avoid quoting issues
        ComposeUp("compose up -d " + serviceList, serviceList);
    }

    /// <summary>
    /// Runs a `docker compose up` invocation, retrying a failed attempt.
    ///
    /// <para>`docker compose up` contacts the registry for any image not already cached, and GitHub's
    /// runners time out against Docker Hub often enough to redden main on their own. On 2026-08-03,
    /// three of the four red main runs were exactly this and nothing else — CIOtel twice and CIMQTT5
    /// once, each dying in under a minute on
    /// `Get "https://registry-1.docker.io/v2/": context deadline exceeded` before a single test ran.
    /// A pull timeout is transient and says nothing about the commit that triggered it, so it should
    /// cost a retry rather than a red build.</para>
    ///
    /// <para>Every failure is retried, not just registry-shaped ones: matching on the message text
    /// would be guessing at a moving target, and a genuinely broken compose file still fails — it
    /// just takes <see cref="DockerComposeAttempts"/> tries to get there, and the exception it throws
    /// carries the real reason either way. Each failed attempt logs the tool's own output, so the
    /// job log always says which it was instead of leaving the reader to infer it.</para>
    /// </summary>
    void ComposeUp(string args, string serviceList)
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

        for (var attempt = 1;; attempt++)
        {
            var process = ProcessTasks
                .StartProcess(toolName, args, logOutput: false, logInvocation: false)
                .AssertWaitForExit();

            if (process.ExitCode == 0)
            {
                if (attempt > 1)
                {
                    Log.Information("docker compose up succeeded for {Services} on attempt {Attempt}",
                        serviceList, attempt);
                }

                return;
            }

            var output = string.Join(Environment.NewLine, process.Output.Select(x => x.Text));

            // Out of attempts: let AssertZeroExitCode throw with the tool's own message.
            if (attempt >= DockerComposeAttempts)
            {
                Log.Error("docker compose up failed for {Services} after {Attempts} attempts:{NewLine}{Output}",
                    serviceList, DockerComposeAttempts, Environment.NewLine, output);
                process.AssertZeroExitCode();
                return;
            }

            var delay = TimeSpan.FromSeconds(5 * attempt);
            Log.Warning(
                "docker compose up failed for {Services} (attempt {Attempt} of {Attempts}), retrying in {Delay}s:{NewLine}{Output}",
                serviceList, attempt, DockerComposeAttempts, delay.TotalSeconds, Environment.NewLine, output);
            Thread.Sleep(delay);
        }
    }

    /// <summary>
    /// Blocks until the given (already-launched) services answer their readiness probes.
    /// </summary>
    void AwaitDockerServices(params string[] services)
    {
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
        if (services.Contains("pulsar"))
            WaitForPulsarToBeReady();
    }

    /// <summary>
    /// Polls until a service reports itself ready. When the budget runs out it restarts the container
    /// and probes once more; a service that fails to serve twice <b>throws</b> and kills the job.
    ///
    /// <para>Every gate in this file used to log a warning and carry on. That is how
    /// <c>CIAzureServiceBus</c> spent 22 of its 25-retry budget on four consecutive <i>green</i> main
    /// runs: the gate declared the emulator ready in under a second, provisioning ran against an api
    /// still answering 503, a class fixture threw, and 22 tests failed together — as flaky tests, not
    /// as infrastructure that never came up. It was found by accident. GH-3783 made that one gate
    /// fatal; this makes the rest of them fatal through a single shared path, so a gate added later
    /// cannot quietly reintroduce the warn-and-continue shape.</para>
    ///
    /// <para>A container that never starts should fail its job in seconds with a message naming the
    /// service, not hand the whole suite to a broker that cannot serve it.</para>
    ///
    /// <para>GH-3855 added the restart. The cost is the worst case: a service that is genuinely dead
    /// now burns twice its budget plus a restart before the job dies, so a budget here is a number
    /// that gets doubled. That is deliberate — these gates are fatal on purpose, there are many of
    /// them across the matrix, and an occurrence that costs a rerun and teaches nothing is the more
    /// expensive of the two.</para>
    /// </summary>
    /// <param name="composeService">
    /// The docker compose service name (e.g. <c>sqlserver</c>), which is what the evidence dump and
    /// the restart address. Deliberately separate from the display name: "SQL Server" is what a
    /// reader of the log wants and <c>sqlserver</c> is what compose answers to.
    /// </param>
    /// <param name="probe">
    /// Returns null when the service is ready, or a short reason why it is not. Exceptions are
    /// treated as "not ready" and their message becomes the reason, so the final error carries the
    /// last real failure rather than a generic timeout.
    /// </param>
    void awaitService(string name, string composeService, TimeSpan budget, Func<string> probe)
    {
        var first = pollForReadiness(name, budget, probe);
        if (first.Ready) return;

        // GH-3855. A gate that expires is not necessarily a gate whose budget was too small. Measured
        // across seven consecutive passing CIPersistence runs, SQL Server was ready on attempt 4, in
        // 28-35s, every time -- 4x headroom against this same 2 minute budget. The run that failed
        // burned all 61 attempts and never came up, which is a different failure, not a slower one:
        // its container took the full 10s SIGTERM grace period to die at teardown, so it was alive
        // and wedged rather than dead. Widening the budget fixes nothing there; restarting it might.
        //
        // The evidence dump comes FIRST, before the restart destroys the container's own account of
        // why it never initialized. That account was missing entirely from the run that prompted
        // this, which is why all that could be established was circumstantial.
        captureServiceEvidence(name, composeService, "before restart");
        restartService(name, composeService);

        var second = pollForReadiness(name, budget, probe);
        if (!second.Ready)
        {
            // Twice is a real signal rather than a wedge, so this is the throw the job dies on --
            // now with two attempts' worth of evidence attached to it.
            captureServiceEvidence(name, composeService, "after restart");

            throw new InvalidOperationException(
                $"{name} was not ready after {budget.TotalSeconds:n0}s ({first.Attempts} attempts), " +
                $"was restarted, and was still not ready after a further {budget.TotalSeconds:n0}s " +
                $"({second.Attempts} attempts). " +
                $"Last attempt before the restart: {first.LastReason} " +
                $"Last attempt after it: {second.LastReason}");
        }

        // Recorded, never reported as clean -- the same rule RetryFailuresInFreshProcess already
        // states. A silent restart would convert a degrading runner or a bad image push into
        // invisible latency, so this lands in the retry ledger where the roll-up diffs it against
        // the previous main run and a trend becomes visible.
        Log.Warning(
            "{Service} was not ready after {Budget}s ({Attempts} attempts), but came up after a restart. " +
            "This is recorded in the retry ledger, NOT treated as a clean start",
            name, budget.TotalSeconds, first.Attempts);

        recordServiceRestart(name, composeService, first.Attempts, first.LastReason, second.Attempts);
    }

    /// <summary>
    /// One pass of the polling loop: returns rather than throws, so <see cref="awaitService"/> owns
    /// what an expired budget means.
    /// </summary>
    static ReadinessResult pollForReadiness(string name, TimeSpan budget, Func<string> probe)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        var lastReason = "no attempt completed";
        var attempts = 0;

        while (true)
        {
            attempts++;

            try
            {
                var reason = probe();
                if (reason is null)
                {
                    Log.Information("{Service} is up and ready (attempt {Attempts})", name, attempts);
                    return new ReadinessResult(true, attempts, null);
                }

                lastReason = reason;
            }
            catch (Exception e)
            {
                lastReason = e.Message;
            }

            if (DateTime.UtcNow >= deadline) return new ReadinessResult(false, attempts, lastReason);

            Thread.Sleep(2000);
        }
    }

    record ReadinessResult(bool Ready, int Attempts, string LastReason);

    /// <summary>
    /// Dumps what the container itself has to say about why it never came up: <c>compose ps</c> for
    /// its state, its own logs, and the host's free memory.
    ///
    /// <para>None of this existed on the failure path before GH-3855, so a wedged container took its
    /// explanation with it -- memory pressure, a bad layer and a genuine startup fault all look
    /// identical from outside. Best effort throughout: reporting on the failure must never replace
    /// the failure, or the job dies with a worse message than the one it already had.</para>
    /// </summary>
    void captureServiceEvidence(string name, string composeService, string moment)
    {
        var grouped = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (grouped) Console.WriteLine($"::group::{name} readiness evidence ({moment})");

        try
        {
            runDiagnostic(composeTool(), $"compose ps {composeService}");
            runDiagnostic(composeTool(), $"compose logs --tail=200 {composeService}");

            // Linux-only, and the runner is the only place this matters -- a developer machine has
            // no OOM killer story worth telling here.
            if (File.Exists("/usr/bin/free")) runDiagnostic("free", "-m");
        }
        finally
        {
            if (grouped) Console.WriteLine("::endgroup::");
        }
    }

    /// <summary>
    /// Restarts a single compose service. Failure here is deliberately not fatal: the throw that
    /// matters is the second readiness failure, and it carries a far better message than "docker
    /// compose restart exited 1" would.
    /// </summary>
    void restartService(string name, string composeService)
    {
        Log.Warning("Restarting the {Service} container ({ComposeService}) and probing once more", name, composeService);
        runDiagnostic(composeTool(), $"compose restart {composeService}");
    }

    /// <summary>
    /// Runs a diagnostic command, logging whatever it produced. Never throws -- see
    /// <see cref="captureServiceEvidence"/>.
    /// </summary>
    static void runDiagnostic(string tool, string arguments)
    {
        try
        {
            var process = ProcessTasks
                .StartProcess(tool, arguments, logOutput: false, logInvocation: false)
                .AssertWaitForExit();

            var output = string.Join(Environment.NewLine, process.Output.Select(x => x.Text));

            Log.Information("$ {Tool} {Arguments} (exit {ExitCode}){NewLine}{Output}",
                tool, arguments, process.ExitCode, Environment.NewLine, output);
        }
        catch (Exception e)
        {
            Log.Warning("Could not run `{Tool} {Arguments}`: {Message}", tool, arguments, e.Message);
        }
    }

    /// <summary>
    /// docker, or podman where that is what is installed. Same resolution <see cref="ComposeUp"/>
    /// uses, so the evidence and restart paths cannot end up talking to a different daemon than the
    /// one that started the container.
    /// </summary>
    static string composeTool()
    {
        bool isAvailable(string toolName)
        {
            try
            {
                ToolPathResolver.GetPathExecutable(toolName);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return new List<string> { "docker", "podman" }.FirstOrDefault(isAvailable) ?? "docker";
    }

    /// <summary>
    /// Opens a TCP connection, returning null when it succeeds. The weakest probe shape available —
    /// a listening socket is not the same claim as a broker that will serve a request — so prefer a
    /// real protocol call wherever the client library is already referenced here.
    /// </summary>
    static string tcpProbe(string host, int port)
    {
        using var tcpClient = new System.Net.Sockets.TcpClient();
        tcpClient.Connect(host, port);
        return null;
    }

    void WaitForPubsubEmulatorToBeReady()
    {
        // The emulator answers HTTP on its main port, so this asks it a question rather than only
        // checking that something is listening — the distinction the ASB gate was built on.
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        awaitService("GCP Pub/Sub emulator", "gcp-pubsub", TimeSpan.FromSeconds(60), () =>
        {
            var response = http.GetAsync("http://localhost:8085/").GetAwaiter().GetResult();

            // Any HTTP answer means the emulator is serving. Deliberately not asserting 200: the
            // lesson from GH-3783 is that pinning a gate to one status code is how it hangs when the
            // service legitimately answers a different one.
            return response is null ? "no response" : null;
        });
    }

    /// <summary>
    /// GH-3799. Pulsar standalone takes tens of seconds to come up and its broker port accepts long
    /// before it will serve, so this asks the admin api a real question instead of checking the
    /// socket — the same distinction that made the Kafka gate below worth rewriting.
    ///
    /// <para>The budget is deliberately generous. This gate is also the thing that turns a Docker
    /// memory shortage into a named failure: previously the suite started its own container per
    /// worker process, and when Docker could not satisfy that the run simply sat at "224 test(s):
    /// 224 batched" forever with nothing pointing at Docker. Timing out here says which service
    /// never came up, in the job that owns it.</para>
    /// </summary>
    void WaitForPulsarToBeReady()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        awaitService("Pulsar", "pulsar", TimeSpan.FromMinutes(3), () =>
        {
            // /admin/v2/brokers/health is the broker's own readiness answer, not merely "something
            // is listening on 8080" — it stays unavailable while the standalone cluster boots.
            var response = http.GetAsync("http://localhost:8080/admin/v2/brokers/health")
                .GetAwaiter().GetResult();

            return response.IsSuccessStatusCode
                ? null
                : $"admin api answered {(int)response.StatusCode} {response.StatusCode}";
        });
    }

    void WaitForKafkaToBeReady()
    {
        // GH-3814: this was TCP-only, which is a far weaker claim than the suite depends on -- and
        // measurably useless. Racing both probes against a cold broker three times, the TCP connect
        // succeeded at 0.0s every time (Docker's port proxy accepts as soon as the container is
        // created, before the broker is listening inside it) while metadata was not served until
        // 2.4-3.0s. So the old gate passed instantly and unconditionally, then handed a ~3s window
        // to a suite that immediately creates topics: "Leader not available", "Not enough replicas"
        // and topic-creation errors, all reading as flaky tests rather than a broker that was not up.
        // Same defect class as the ASB emulator gate in GH-3783, which cost 22 retries per run
        // across four green main runs before anyone noticed.
        //
        // A metadata request is the real question: it round-trips the Kafka protocol and does not
        // answer until the broker will serve.
        var config = new AdminClientConfig
        {
            BootstrapServers = "localhost:9092",
            SocketTimeoutMs = 5000,

            // Without this the client spends the full default timeout on its own internal retries
            // before surfacing anything, which would blur the gate's own attempt/budget accounting.
            ApiVersionRequestTimeoutMs = 5000
        };

        // librdkafka writes connection failures straight to stderr while the broker is coming up.
        // awaitService already reports every attempt and carries the last real failure into the
        // final error, so silence the duplicate stream rather than print a scary log for what is
        // simply "not up yet".
        using var admin = new AdminClientBuilder(config)
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();

        awaitService("Kafka", "kafka", TimeSpan.FromSeconds(60), () =>
        {
            // Throws until the broker will actually serve; awaitService turns that into the reason.
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(5));

            // A broker mid-startup can answer with an empty cluster view, which is still not a
            // broker that will accept a topic creation.
            return metadata.Brokers.Count == 0 ? "metadata returned no brokers" : null;
        });
    }

    void WaitForSqlServerToBeReady()
    {
        // Budget is now wall-clock rather than an attempt count. The old "60 seconds" in the warning
        // was never true: each failed attempt could burn the connection's own 5s timeout before the
        // 2s sleep, so 30 attempts was anywhere from 60s to 210s depending on how the failure came
        // back. A deadline says what it means.
        awaitService("SQL Server", "sqlserver", TimeSpan.FromMinutes(2), () =>
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                "Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;Encrypt=False");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    void WaitForMySqlToBeReady()
    {
        awaitService("MySQL", "mysql", TimeSpan.FromMinutes(2), () =>
        {
            using var conn = new MySqlConnector.MySqlConnection(
                "Server=localhost;Port=3306;Database=wolverine;User=root;Password=P@55w0rd;");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    void WaitForOracleToBeReady()
    {
        // Oracle is the slowest image in the compose file to reach a usable state, hence the roomiest
        // budget. Connecting as the application user against FREEPDB1 (rather than pinging the
        // listener) is deliberate: the listener answers well before the pluggable database will
        // accept an application login.
        awaitService("Oracle", "oracle", TimeSpan.FromMinutes(3), () =>
        {
            using var conn = new Oracle.ManagedDataAccess.Client.OracleConnection(
                "User Id=wolverine;Password=wolverine;Data Source=localhost:1521/FREEPDB1");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM DUAL";
            cmd.ExecuteNonQuery();
            return null;
        });
    }

    void WaitForLocalStackToBeReady()
    {
        using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        awaitService("LocalStack", "localstack", TimeSpan.FromMinutes(2), () =>
        {
            var response = httpClient.GetAsync("http://localhost:4566/_localstack/health")
                .GetAwaiter().GetResult();

            return response.IsSuccessStatusCode ? null : $"health endpoint answered {(int)response.StatusCode}";
        });
    }

    /// <summary>
    /// Waits for the Azure Service Bus emulator to be ready for <b>provisioning</b>, not merely accepting
    /// sockets.
    ///
    /// <para>The emulator binds its AMQP listener on 5673 as soon as Kestrel starts, but its MANAGEMENT api
    /// on 5300 — the one <c>ServiceBusAdministrationClient</c> uses to create queues and topics — answers
    /// <c>503 "Service is warming up. Please try after some time."</c> for a good while after that. This
    /// gate used to be a bare TCP connect to 5673, so it announced "up and ready!" while every provisioning
    /// call still failed. The first test class to provision anything (<c>InlineComplianceFixture</c>) threw
    /// out of <c>InitializeAsync</c>, which fails every test in its class — all 22 of them — and the
    /// standing retry policy then reran each one alone in a fresh process, by which time the emulator was
    /// warm. So the job reported GREEN while burning 22 of its 25-retry budget on every single run, and
    /// those 22 were 85% of all retries in the entire repository.</para>
    ///
    /// <para>It went unnoticed for so long partly because it does not reproduce locally: docker-compose
    /// pinned these images to <c>:latest</c>, and <c>servicebus-emulator:latest</c> moved from 2.0.0 to
    /// 2.0.1, which warms up more slowly. CI pulls fresh every run and gets 2.0.1; a developer machine
    /// keeps whatever it pulled months ago. The images are pinned now for exactly that reason.</para>
    /// </summary>
    void WaitForAzureServiceBusEmulatorToBeReady()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Any management request will do. While warming, the emulator answers 503 to all of them; once warm
        // it answers something else (200/400/401 depending on auth and api-version), so readiness does not
        // depend on getting the ATOM api-version right here.
        const string managementProbe = "http://localhost:5300/$Resources/topics";

        // Deliberately fatal, via the same shared path as every other gate. This used to log a warning
        // and carry on, so an emulator that never came up at all still fed the entire suite into a
        // broker that could not serve it — and the resulting failures read as flaky tests rather than
        // as infrastructure that never started.
        awaitService("Azure Service Bus emulator", "asb-emulator", TimeSpan.FromMinutes(3), () =>
        {
            using (var tcpClient = new System.Net.Sockets.TcpClient())
            {
                tcpClient.Connect("localhost", 5673);
            }

            var response = http.GetAsync(managementProbe).GetAwaiter().GetResult();

            // Keyed on "not 503", NOT on "== 200". CI answers 400 here and a developer machine
            // answers 200; asserting 200 would hang for the whole budget and then fail the job. This
            // distinction cost a day to find — do not tighten it.
            return response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                ? "the management api on 5300 is still warming up (503)"
                : null;
        });
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

    /// <summary>
    /// GH-3566 / GH-4007: the claim-check backend suites had no CI target at all -- every backend shipped
    /// with tests that only ever ran on a developer machine, and were merely *compiled* by the
    /// full-solution build. This runs all seven, database-backed and object-store alike.
    /// </summary>
    /// <remarks>
    /// The object-store suites keep their own per-emulator skip guards so a developer without, say,
    /// Azurite running still gets a clean local run. The point of this target is that CI has every
    /// emulator, so nothing skips there. Azurite in particular was missing from the compose file
    /// entirely, which meant the Azure Blob suite skipped everywhere -- see GH-4007.
    /// </remarks>
    Target CIClaimCheck => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            AbsolutePath suite(string name) =>
                RootDirectory / "src" / "Persistence" / $"Wolverine.ClaimCheck.{name}.Tests" /
                $"Wolverine.ClaimCheck.{name}.Tests.csproj";

            var databaseSuites = new[] { suite("Postgresql"), suite("SqlServer"), suite("Marten") };
            var objectStoreSuites = new[]
            {
                suite("AmazonS3"), suite("AzureBlobStorage"), suite("GoogleCloudStorage"), suite("Nats")
            };

            var all = databaseSuites.Concat(objectStoreSuites).ToArray();

            BuildTestProjects(all);
            StartDockerServices("postgresql", "sqlserver", "localstack", "azurite", "fake-gcs-server", "nats");

            foreach (var project in all)
            {
                RunTestProject(project);
            }
        });

    Target CISqlite => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var sqliteTests = RootDirectory / "src" / "Persistence" / "SqliteTests" / "SqliteTests.csproj";

            BuildTestProjects(sqliteTests);

            RunTestProject(sqliteTests);
        });

    // GH-3907. Fisher is a SQLite file, so this needs no container at all - which is also why it is
    // its own target rather than a passenger on someone else's: there is nothing to share. Being in
    // wolverine.slnx makes the solution build catch a COMPILE break; only running it catches a
    // behavioural one, and FisherTests is the acceptance test for the whole GH-3907 unification.
    // PolecatIncidentService.Tests sat un-compilable for four months for exactly the want of this.
    Target CIFisher => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var fisherTests = RootDirectory / "src" / "Persistence" / "FisherTests" / "FisherTests.csproj";

            BuildTestProjects(fisherTests);

            RunTestProject(fisherTests);
        });

    Target CISqlServer => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var sqlServerTests = RootDirectory / "src" / "Persistence" / "SqlServerTests" / "SqlServerTests.csproj";

            // SQL Server's boot is one of the slower container starts; overlap it with the compile
            // instead of paying the two serially.
            LaunchDockerServices("sqlserver");
            BuildTestProjects(sqlServerTests);
            AwaitDockerServices("sqlserver");

            // Profiled 2026-08-01 (TRX over the full suite, local M-series): 60 classes, 931s of
            // test time, largest class 167s -> a 5.6x ceiling. Hosted runners clamp 4 workers to 2,
            // where the LPT bound is ~470s of test wall clock against ~860s sequential. The sibling
            // databases the multi-tenancy and NSB dedicated-db suites create are lane-scoped via
            // LaneDatabases.Name, so lanes cannot collide on them.
            RunTestProject(sqlServerTests, workers: 4, sqlServerDatabasePerLane: true);
        });

    // MartenTests was the 18m long pole of the matrix (#3752). It was first attacked with worker lanes —
    // four processes, each on its own database via WOLVERINE_POSTGRES — and that halved the wall clock but
    // did not hold up: the runner itself was killed outright ("The runner has received a shutdown signal")
    // twice in a row at ~15 minutes on an unrelated diff (#3771), which is the OOM signature, not a test
    // failure. Several Marten test hosts plus Postgres does not fit a 4-vCPU/16GB hosted runner, and a lane
    // count that dies to the OOM killer on a busy day is not a stable halving.
    //
    // So the wall clock is bought the way CIPolecat buys it (#3350): SEGMENT the suite across parallel CI
    // JOBS, each running strictly sequentially against exactly ONE database. Concurrency moves off the
    // runner VM and onto the matrix, where every shard gets its own 16GB. This also closes #3762 — the
    // sibling databases some suites create beside the primary (tenant1-3, players, things) are fixed names,
    // and with a single lane per job they have a single owner again, so no lane-scoping sweep is needed.
    //
    // Balanced by MEASURED per-namespace duration, from `MartenTests --output Detailed` over the full suite
    // (533 tests, 880s, zero failures). This matters: the first cut of this split was balanced by test-CLASS
    // count on the PolecatTests precedent, and class count turned out to be very nearly an *inverted*
    // predictor of cost here. The expensive namespaces are the ones with few, slow tests:
    //
    //     Distribution     192.6s / 49 tests    (multi-node agent distribution, real leader election)
    //     MultiTenancy     148.4s / 53 tests    (creates tenant databases)
    //     Bugs             126.5s / 53 tests
    //     AggregateHandlerWorkflow  73.4s / 90 tests   <- most tests, a third of Distribution's cost
    //
    // Distribution and MultiTenancy alone are 39% of the suite, and the class-count split had put BOTH in
    // the same shard — which is why that shard came in at 7m56s against 4m22s and 4m39s for the other two.
    // The split below is the best of all 3-way assignments, found by brute force over the measured totals:
    //
    //   CIMartenDistribution  Distribution, AggregateHandlerWorkflow,
    //                         AncillaryStores, Requirements, Sample              293.6s
    //   CIMartenTenancy       MultiTenancy, TestHelpers, ScheduledJobs,
    //                         Persistence (incl. Persistence.Sagas), Dcb         293.4s
    //   CIMarten              everything else (root namespace, Bugs, Saga)
    //                         + MartenSubscriptionTests                          293.5s
    //
    // A 0.2s spread on the measured numbers. Local timings on an M-series machine, so CI's absolute values
    // differ, but the relative costs are what the split keys off.
    //
    // The shards are named for what dominates them. Note that `MartenTests.Saga` lands in the catch-all
    // while `MartenTests.Persistence.Sagas` lands in CIMartenTenancy — the balance does not respect theme,
    // which is exactly why the names track the dominant namespace rather than pretending otherwise.
    //
    // Defined ONCE below: the two named shards list their namespaces and CIMarten is the *negation* of both,
    // so a brand new namespace lands in CIMarten automatically instead of being silently dropped from CI.
    //
    // Note the root namespace `MartenTests` cannot be an *include* list entry — every shard's display names
    // start with it — which is precisely why the catch-all has to be the negation.

    AbsolutePath MartenTests => RootDirectory / "src" / "Persistence" / "MartenTests" / "MartenTests.csproj";

    AbsolutePath MartenSubscriptionTests =>
        RootDirectory / "src" / "Persistence" / "MartenSubscriptionTests" / "MartenSubscriptionTests.csproj";

    static readonly string[] MartenDistributionNamespaces =
    [
        "MartenTests.Distribution",
        "MartenTests.AggregateHandlerWorkflow",
        "MartenTests.AncillaryStores",
        "MartenTests.Requirements",
        "MartenTests.Sample"
    ];

    // "MartenTests.Persistence" also claims MartenTests.Persistence.Sagas, which is where most of that
    // subtree's tests live — the prefix match is on "MartenTests.Persistence." so both are covered.
    static readonly string[] MartenTenancyNamespaces =
    [
        "MartenTests.MultiTenancy",
        "MartenTests.TestHelpers",
        "MartenTests.ScheduledJobs",
        "MartenTests.Persistence",
        "MartenTests.Dcb"
    ];

    /// <param name="testFilter">Which slice of MartenTests this shard owns.</param>
    /// <param name="alsoSubscriptions">
    /// MartenSubscriptionTests is a single 12-test class, too small to shard and too small to justify a job
    /// of its own, so it rides along with the catch-all shard.
    /// </param>
    void runMartenShard(Func<WorkerTest, bool> testFilter, bool alsoSubscriptions = false)
    {
        AbsolutePath[] projects = alsoSubscriptions
            ? [MartenTests, MartenSubscriptionTests]
            : [MartenTests];

        BuildTestProjects(projects);
        StartDockerServices("postgresql");

        // workers: 1 and no postgresDatabasePerLane, deliberately. One database is active at a time —
        // that is the whole point of segmenting rather than lane-splitting. See the note above.
        RunTestProjects(projects.Select(x => x.ToString()).ToArray(), testFilter: testFilter);
    }

    /// <summary>
    /// Marten shard 1: multi-node agent distribution — the single most expensive namespace in the suite —
    /// plus the aggregate handler workflow tests and ancillary stores.
    /// </summary>
    Target CIMartenDistribution => _ => _
        .ProceedAfterFailure()
        .Executes(() => runMartenShard(includeNamespaces(MartenDistributionNamespaces)));

    /// <summary>
    /// Marten shard 2: multi-tenancy (the second most expensive namespace, since it provisions tenant
    /// databases), the test helpers, scheduled jobs, saga persistence and DCB.
    /// </summary>
    Target CIMartenTenancy => _ => _
        .ProceedAfterFailure()
        .Executes(() => runMartenShard(includeNamespaces(MartenTenancyNamespaces)));

    /// <summary>
    /// Marten shard 3: everything not claimed by CIMartenDistribution or CIMartenTenancy — the root
    /// namespace, Bugs, Saga, and any newly added namespace, which lands here by default — plus
    /// MartenSubscriptionTests.
    /// </summary>
    Target CIMarten => _ => _
        .ProceedAfterFailure()
        .Executes(() => runMartenShard(
            excludeNamespaces([..MartenDistributionNamespaces, ..MartenTenancyNamespaces]),
            alsoSubscriptions: true));

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
    static bool ComplianceBatteries(WorkerTest t) => t.DisplayName.Contains("SendingAndReceivingCompliance");
    static bool NotComplianceBatteries(WorkerTest t) => !ComplianceBatteries(t);

    AbsolutePath AmazonSqsTests => RootDirectory / "src" / "Transports" / "AWS" / "Wolverine.AmazonSqs.Tests" / "Wolverine.AmazonSqs.Tests.csproj";
    AbsolutePath AmazonSnsTests => RootDirectory / "src" / "Transports" / "AWS" / "Wolverine.AmazonSns.Tests" / "Wolverine.AmazonSns.Tests.csproj";

    void runAwsShard(AbsolutePath project, Func<WorkerTest, bool> testFilter = null)
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

    Target CIMQTT5 => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Transports" / "MQTT" / "Wolverine.Mqtt5.Tests" / "Wolverine.Mqtt5.Tests.csproj";

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

            // The global partitioning topology forces durable mode on every slot, so that suite
            // needs a real message store. See #3467.
            //
            // GH-3799: Pulsar now comes from docker-compose rather than Testcontainers. The fixture's
            // [ModuleInitializer] starts a container per *process*, and the supervisor partitions this
            // project across worker processes (2-24 observed, plus a fresh process per retried test) —
            // so one job could ask Docker for that many concurrent Pulsar standalone brokers, an image
            // that is not small. One compose broker, started once and shared, is the whole fix.
            StartDockerServices("pulsar", "postgresql");

            // The fixture already honours these ("an already-running Pulsar wins") and they were set
            // nowhere. With them set, no worker process starts a container at all.
            Environment.SetEnvironmentVariable("WOLVERINE_PULSAR", "pulsar://localhost:6650");
            Environment.SetEnvironmentVariable("WOLVERINE_PULSAR_HTTP", "http://localhost:8080");

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

            // Was a bare DotNetTest, which made this the only test target without the flaky-retry
            // harness -- so a single intermittent test failed the whole http workflow job, where
            // the same flake in CIRabbitMQ or CIMarten is retried and reported as flaky-but-passing.
            // It also missed the standard Category!=Flaky filter that RunTestProject applies.
            // See GH-3705.
            RunTestProject(tests);
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

    /// <summary>
    /// TracingTests asserts that correlation and causation IDs survive an HTTP invoke, a local
    /// message, a TCP hop and two RabbitMQ subscribers -- the OpenTelemetry plumbing no unit test
    /// covers. It had rotted to the point of not compiling (GH-3704), because nothing built it: it
    /// was outside wolverine.slnx *and* outside every CI target. It is in the solution now, and this
    /// target keeps it honest by actually running it.
    /// </summary>
    Target CIOtel => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var tests = RootDirectory / "src" / "Testing" / "OpenTelemetry" / "TracingTests" / "TracingTests.csproj";

            BuildTestProjects(tests);
            StartDockerServices("rabbitmq");

            RunTestProject(tests);
        });

    /// <summary>
    /// GH-3779 / GH-3781. <c>SlowTests</c> has never run in <b>any</b> CI workflow — no job, no Nuke
    /// target — even though it holds the only real-host reproductions of the GH-3753 agent
    /// assignment chain (<c>SlowTests/Agents</c>: three multi-node classes over a 480-agent universe).
    /// A reproduction nothing runs is a reproduction that rots, and GH-3781 is exactly what it caught
    /// the moment anyone did run it: a Balanced-mode host that would not finish <c>StopAsync</c>.
    ///
    /// <para>Postgres is the only infrastructure the project needs — the SqlServer and Kafka project
    /// references are transitive, and nothing here opens either.</para>
    ///
    /// <para>This is deliberately one unsharded job to begin with: the point is to <i>measure</i> what
    /// the suite costs on a hosted runner, against the same 20 minute cap every other job answers to.
    /// If it does not fit, the answer is to shard it the way CIMarten and CIPolecat were sharded (see
    /// #3350), balanced on the measured per-class durations this job prints — not to raise the cap.</para>
    /// </summary>
    Target CISlowTests => _ => _
        .ProceedAfterFailure()
        .Executes(() =>
        {
            var slowTests = RootDirectory / "src" / "Testing" / "SlowTests" / "SlowTests.csproj";

            // The agent scale classes are wall-clock bound, so overlap the container boot with the
            // compile rather than paying them serially.
            LaunchDockerServices("postgresql");
            BuildTestProjects(slowTests);
            AwaitDockerServices("postgresql");

            RunTestProject(slowTests);
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

    // ─── Azure Service Bus CI Targets ──────────────────────────────────
    //
    // GH-3790. Wolverine.AzureServiceBus.Tests was the tests.yml wall-clock pole and sat at 84% of the
    // hard 20 minute job cap (1012s of 1200s, stable over six runs), with no headroom for the next
    // batch of ASB tests. It is now sharded three ways.
    //
    // Sharding was deliberately abandoned once before, and correctly: at the time 57 of the suite's 68
    // minutes were broker timeouts, so splitting would have spread a real defect across three jobs and
    // hidden it a second time. GH-3789 removed those timeouts, so what is left is genuine capacity.
    //
    // Measured, not guessed (Release, net9.0, serial, freshly recreated emulator in docker):
    // 300 tests, 806s of test-body time across 42 classes, 14m36s wall clock. Two properties of that
    // measurement shaped the split:
    //
    //   - Unlike PolecatTests, per-class fixture cost does NOT dominate here: the test bodies account
    //     for 806s of the 876s wall clock, so balancing on measured test time translates almost
    //     directly into balanced jobs.
    //   - The distribution is extremely top-heavy. 21 of the 42 classes are pure unit tests costing
    //     ~0s, while leader_election alone is 212s — 26% of the entire suite.
    //
    // Namespaces could not carry this split (264 of 306 tests live in the root namespace), so the
    // shards are keyed on class prefixes. includeNamespaces/excludeNamespaces are prefix matchers and
    // work unchanged for that — see the note on them.
    //
    // Resulting balance, and the reason the catch-all is deliberately the SMALLEST of the three:
    // new tests land in the catch-all, so it is the shard that must have room to grow.
    //
    //   Leader   280.3s   21 tests   34.8%
    //   Routing  277.6s   27 tests   34.4%
    //   catch-all 248.1s 252 tests   30.8%
    //
    // Each shard is its own matrix job on its own runner, so each gets its own emulator container.
    // That also makes the emulator's 50-queue-per-namespace cap (SubCode=40300, reported as a
    // broker-initialization timeout that never mentions a cap) strictly less likely than before,
    // because each shard now declares a fraction of the queues the single job used to.
    //
    // A couple of members sit outside what their shard's name suggests — StatefulResourceSmokeTests
    // with leader election, using_native_scheduling with conventional routing. That is balance
    // talking, not taxonomy, and it is why the numbers above are recorded here.

    /// <summary>
    /// ASB shard 1: the two most expensive classes in the suite, 34.8% of its measured time in
    /// 21 tests.
    /// </summary>
    static readonly string[] AsbLeaderShard =
    [
        "Wolverine.AzureServiceBus.Tests.leader_election",            // 212.3s / 15 tests — 26% of the suite
        "Wolverine.AzureServiceBus.Tests.StatefulResourceSmokeTests"  //  68.0s /  6 tests
    ];

    /// <summary>
    /// ASB shard 2: the whole ConventionalRouting namespace, plus native scheduling to balance it.
    /// </summary>
    static readonly string[] AsbRoutingShard =
    [
        "Wolverine.AzureServiceBus.Tests.ConventionalRouting",        // 221.6s / 22 tests — a real namespace
        "Wolverine.AzureServiceBus.Tests.using_native_scheduling"     //  55.9s /  5 tests
    ];

    void runAzureServiceBusShard(Func<WorkerTest, bool> testFilter)
    {
        var tests = RootDirectory / "src" / "Transports" / "Azure" / "Wolverine.AzureServiceBus.Tests" / "Wolverine.AzureServiceBus.Tests.csproj";

        BuildTestProjects(tests);
        // Postgres is needed for leader election tests. Started for every shard rather than only the
        // one holding leader_election: durability-backed tests are spread across all three, and the
        // container is cheap next to the emulator's warm-up.
        StartDockerServices("asb-emulator", "postgresql");

        RunTestProject(tests, testFilter: testFilter);
    }

    /// <summary>
    /// ASB shard 1 — leader election and stateful resources.
    /// </summary>
    Target CIAzureServiceBusLeader => _ => _
        .ProceedAfterFailure()
        .Executes(() => runAzureServiceBusShard(includeNamespaces(AsbLeaderShard)));

    /// <summary>
    /// ASB shard 2 — conventional routing and native scheduling.
    /// </summary>
    Target CIAzureServiceBusRouting => _ => _
        .ProceedAfterFailure()
        .Executes(() => runAzureServiceBusShard(includeNamespaces(AsbRoutingShard)));

    /// <summary>
    /// ASB shard 3 — everything else, so a newly added class always runs somewhere.
    /// </summary>
    Target CIAzureServiceBus => _ => _
        .ProceedAfterFailure()
        .Executes(() => runAzureServiceBusShard(
            excludeNamespaces([..AsbLeaderShard, ..AsbRoutingShard])));

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
    /// <summary>
    /// Shard filter over test-name prefixes. A prefix is usually a namespace, but since a test's
    /// DisplayName is <c>Namespace.Class.Method</c> the same match works for a fully-qualified CLASS
    /// name — which is what the Azure Service Bus shards use, because 264 of that project's 306 tests
    /// live in the root namespace and namespaces cannot split it (GH-3790).
    /// </summary>
    static Func<WorkerTest, bool> includeNamespaces(params string[] namespaces)
    {
        return t => namespaces.Any(n => t.DisplayName.StartsWith(n + "."));
    }

    static Func<WorkerTest, bool> excludeNamespaces(params string[] namespaces)
    {
        return t => !namespaces.Any(n => t.DisplayName.StartsWith(n + "."));
    }

    void runPolecatShard(Func<WorkerTest, bool> testFilter)
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
        .Executes(() =>
        {
            runPolecatShard(excludeNamespaces([..PolecatWorkflowNamespaces, ..PolecatSagaNamespaces]));

            // GH-3839. This ran in no CI target at all, which is half of why it sat un-compilable
            // for four months. Re-enabling it in wolverine.slnx makes the solution build catch a
            // *compile* break; only running it catches a behavioural one, and the sample is the
            // worked example a reader follows.
            //
            // Lives on this shard because it is the catch-all and because runPolecatShard has
            // already started the SQL Server the sample connects to. ~9s against a 338s job.
            BuildTestProjectsWithFramework("net10.0", PolecatIncidentServiceTests);
            RunTestProject(PolecatIncidentServiceTests, frameworkOverride: "net10.0");
        });

    AbsolutePath PolecatIncidentServiceTests => RootDirectory / "src" / "Persistence" / "Polecat" /
                                                "PolecatIncidentService.Tests" / "PolecatIncidentService.Tests.csproj";
}
