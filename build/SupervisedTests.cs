using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bobcat.Resilience;
using Bobcat.Supervisor;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

// The supervised test runner: every CI target runs its test projects through Bobcat's Supervisor
// (https://github.com/JasperFx/bobcat) instead of `dotnet test`. What that buys over the old
// TRX-parse-and-retry harness:
//
//   - Worker processes. The suite executable (an xUnit v3 Microsoft.Testing.Platform host — see
//     UseMicrosoftTestingPlatformRunner in Directory.Build.props) is driven over the MTP wire
//     protocol, so a target can split a suite across `workers` processes. Partitioning is by test
//     CLASS, never by test — splitting a class across processes breaks every isolation contract
//     xUnit writes against static state, measured as 1-4 non-deterministic failures on
//     PersistenceTests when it was tried per-test.
//   - Per-lane environments. With `postgresDatabasePerLane`, each worker process is pointed at its
//     own database via WOLVERINE_POSTGRES (see src/Servers.cs), because schema names are
//     hard-coded throughout the suites — isolation must be the database, not the connection alone.
//   - Honest retries. A first failure is retried once in a FRESH process (parity with the old
//     harness's fresh `dotnet test` invocation), but a pass-on-retry is never folded into a clean
//     pass: it is reported as the flakiness ledger, and a crashed worker is Indeterminate — with
//     the worker's exit code and stderr — never a silent pass or an ordinary "failed".
//
// Reliability is the acceptance criterion, not speed: pass counts are reported beside every run,
// and `--disable-test-retry` turns retries off entirely for measuring, since a retry budget masks
// exactly the instability parallelism is suspected of introducing.
partial class Build
{
    /// <summary>
    /// Overrides every target's worker count from the command line (e.g. <c>--test-workers 4</c>).
    /// For measuring; the committed per-target counts are the tuned values.
    /// </summary>
    [Parameter] readonly int? TestWorkers;

    /// <summary>
    /// Turns off the retry-once policy (<c>--disable-test-retry</c>). Measure with retries off
    /// first: 144/144 green at every fleet size is a much stronger claim without a retry budget.
    /// </summary>
    [Parameter] readonly bool DisableTestRetry;

    /// <summary>
    /// The standing exclusion every CI target applies: tests tagged [Trait("Category", "Flaky")]
    /// do not run. Same semantics as the old vstest `Category!=Flaky` filter.
    /// </summary>
    static bool NotFlaky(WorkerTest test)
        => !(test.Traits.TryGetValue("Category", out var category) && category.Contains("Flaky"));

    /// <summary>
    /// Runs one test project through the supervisor. See <see cref="RunTestProjects"/>.
    /// </summary>
    void RunTestProject(string projectPath, string frameworkOverride = null,
        Func<WorkerTest, bool> testFilter = null, int workers = 1, bool postgresDatabasePerLane = false,
        bool sqlServerDatabasePerLane = false)
    {
        RunTestProjects([projectPath], frameworkOverride, testFilter, workers, postgresDatabasePerLane,
            sqlServerDatabasePerLane);
    }

    /// <summary>
    /// Runs test projects through Bobcat's supervisor, sequentially per project.
    /// </summary>
    /// <param name="testFilter">
    /// Optional shard filter ANDed onto the standing <see cref="NotFlaky"/> exclusion. Used by the
    /// sharded CI targets (see #3350) to split one heavy project across parallel CI jobs. A shard
    /// filter that matches nothing fails the run — a rename that empties a shard must not read as
    /// a green job.
    /// </param>
    /// <param name="workers">
    /// Worker processes for the suite. 1 (sequential) unless the suite has been profiled: the
    /// largest test class is a hard floor on wall clock, so compute
    /// <c>sum(durations)/largest class</c> before raising this — if the ceiling is 3x, eight
    /// workers buy nothing but containers.
    /// </param>
    /// <param name="postgresDatabasePerLane">
    /// Provisions one Postgres database per worker (wolverine_w0..N-1 on the docker-compose
    /// server) and points each lane at its own via WOLVERINE_POSTGRES. Required before raising
    /// <paramref name="workers"/> on any Postgres-backed suite.
    /// </param>
    /// <param name="sqlServerDatabasePerLane">
    /// Same isolation story for SQL Server: one database per worker (wolverine_w0..N-1 on the
    /// docker-compose server, port 1434), each lane pointed at its own via WOLVERINE_SQLSERVER.
    /// One server, many databases — a fleet of SQL Server CONTAINERS is far past what a 4-vCPU/16GB
    /// hosted runner can carry (see the worker-clamp note in runSupervised).
    /// </param>
    void RunTestProjects(string[] projectPaths, string frameworkOverride = null,
        Func<WorkerTest, bool> testFilter = null, int workers = 1, bool postgresDatabasePerLane = false,
        bool sqlServerDatabasePerLane = false)
    {
        var failedProjects = new List<string>();
        foreach (var projectPath in projectPaths)
        {
            foreach (var framework in frameworksBuiltFor(projectPath, frameworkOverride))
            {
                if (!runSupervised(projectPath, framework, testFilter, TestWorkers ?? workers,
                        postgresDatabasePerLane, sqlServerDatabasePerLane))
                    failedProjects.Add($"{projectPath} ({framework})");
            }
        }

        if (failedProjects.Count > 0)
            throw new InvalidOperationException($"Tests failed: {string.Join(", ", failedProjects)}");
    }

    bool runSupervised(string projectPath, string framework, Func<WorkerTest, bool> shardFilter,
        int workers, bool postgresDatabasePerLane, bool sqlServerDatabasePerLane = false)
    {
        // The committed per-target count is tuned on a developer machine; the machine actually
        // running decides what it can carry. A GitHub-hosted runner has 4 vCPUs and 16GB, and 4
        // concurrent Marten test hosts beside Postgres got the runner itself killed with a
        // shutdown signal — an oversubscribed fleet does not fail politely. An explicit
        // --test-workers bypasses the clamp: measuring past the ceiling is a valid thing to ask.
        var ceiling = Math.Max(1, Environment.ProcessorCount / 2);
        if (TestWorkers is null && workers > ceiling)
        {
            Log.Information("Clamping {Asked} workers to {Ceiling} for this machine's {Cores} core(s)",
                workers, ceiling, Environment.ProcessorCount);
            workers = ceiling;
        }

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var executable = testHostFor(projectPath, framework);

        Log.Information("=== {Project} ({Framework}): supervised run, {Workers} worker(s){Retry} ===",
            projectName, framework, workers, DisableTestRetry ? ", retries OFF" : "");

        var factory = new MtpWorkerFactory(executable)
        {
            EnvironmentFor = laneEnvironment(workers, postgresDatabasePerLane, sqlServerDatabasePerLane)
        };

        var supervisor = new Supervisor(factory)
        {
            MaxParallelWorkers = workers,
            TestFilter = shardFilter is null ? NotFlaky : t => NotFlaky(t) && shardFilter(t),
            RetryBudget = DisableTestRetry
                ? RetryBudget.None
                : new RetryBudget { MaxAttemptsPerTest = 3, MaxRetriesPerRun = 25 },
            // The policy below only ever asks for fresh-process retries, so idle lanes are
            // released before they run: without this, workers+1 test hosts sit resident at once,
            // which OOM-killed 16GB GitHub runners twice — both times during a retry.
            ReleaseIdleLanes = true,
            Log = message => Log.Information("  {Message}", message)
        };

        if (!DisableTestRetry) supervisor.AddFailurePolicy(new RetryFailuresInFreshProcess());

        var results = supervisor.Run().GetAwaiter().GetResult();

        return report(projectName, results, shardFilter is not null);
    }

    /// <summary>
    /// Parity with the old flaky-retry harness, which gave a failed test up to THREE attempts:
    /// the suite pass, then RunTestWithRetry's two single-test `dotnet test` invocations — each
    /// a fresh, quiet process. Fresh-process isolation matters: a warm-process retry was tried
    /// here and produced retries that could never succeed, because a failed first attempt leaves
    /// node registrations and half-built state behind in the process (MySqlTests'
    /// end_to_end_from_scratch cannot run "from scratch" in a process where scratch no longer
    /// exists). ReleaseIdleLanes above keeps the memory profile bounded instead. A pass on any
    /// retry is reported as flaky, never as clean.
    /// </summary>
    class RetryFailuresInFreshProcess : IFailurePolicy
    {
        public Disposition Decide(AttemptContext attempt)
        {
            if (attempt.Succeeded || !attempt.RetriesAvailable) return null;

            return Disposition.RetryInFreshProcess(
                "a failure is retried in a fresh process, within the budget, to separate flaky from broken");
        }
    }

    bool report(string projectName, SupervisorResults results, bool hadShardFilter)
    {
        if (results.AbortReason is not null)
        {
            Log.Error("=== {Project}: run ABORTED — {Reason} ===", projectName, results.AbortReason);
            return false;
        }

        if (results.Tests.Count == 0)
        {
            // MTP silently ignoring a bad filter and running everything is the trap this guards
            // against, inverted: a filter matching nothing is indistinguishable from a shard
            // whose namespaces were renamed away. That must not read as a green job.
            if (hadShardFilter)
            {
                Log.Error("=== {Project}: the shard filter matched NO tests — renamed namespaces? ===", projectName);
                return false;
            }

            Log.Warning("=== {Project}: no tests ran (all excluded or none discovered) ===", projectName);
            return true;
        }

        Log.Information("=== {Project}: {Summary} ===", projectName, results.Summarize());

        foreach (var flaky in results.PassedOnRetry)
            Log.Warning("  [FLAKY] {Test} — passed on attempt {Attempts}", flaky.DisplayName, flaky.AttemptCount);

        foreach (var fault in results.WorkerFaults)
            Log.Error("  [WORKER FAULT] {Fault}", fault);

        foreach (var test in results.Indeterminate)
            Log.Error("  [INDETERMINATE] {Test} — {Error}", test.DisplayName, test.Final.Outcome.ErrorMessage);

        foreach (var test in results.Failed)
            Log.Error("  [FAILED] {Test} — {Error}", test.DisplayName, test.Final.Outcome.ErrorMessage);

        return results.ExitCode == 0;
    }

    // ─── Test host resolution ──────────────────────────────────────────

    /// <summary>
    /// The frameworks a supervised run should cover: the override or --framework when given,
    /// otherwise every target framework the project has actually been built for — matching the
    /// old bare `dotnet test`, which ran all TFMs.
    /// </summary>
    IReadOnlyList<string> frameworksBuiltFor(string projectPath, string frameworkOverride)
    {
        var chosen = frameworkOverride ?? Framework;
        if (!string.IsNullOrEmpty(chosen)) return [chosen];

        var binDir = (AbsolutePath)Path.GetDirectoryName(projectPath) / "bin" / Configuration;
        if (!Directory.Exists(binDir))
            throw new InvalidOperationException($"{binDir} does not exist — build {projectPath} first.");

        var frameworks = Directory.GetDirectories(binDir)
            .Select(Path.GetFileName)
            .Where(tfm => File.Exists(testHostPathFor(projectPath, tfm)))
            .OrderBy(tfm => tfm)
            .ToList();

        if (frameworks.Count == 0)
            throw new InvalidOperationException(
                $"No test host executable under {binDir}. Build the project first; the executable comes " +
                "from UseMicrosoftTestingPlatformRunner in Directory.Build.props.");

        return frameworks;
    }

    string testHostFor(string projectPath, string framework)
    {
        var executable = testHostPathFor(projectPath, framework);
        if (!File.Exists(executable))
            throw new InvalidOperationException(
                $"{executable} does not exist — build {projectPath} for {framework} first. A build without " +
                "UseMicrosoftTestingPlatformRunner yields a non-supervisable executable at the same path, so " +
                "prefer fixing the build over deleting this check.");

        return executable;
    }

    string testHostPathFor(string projectPath, string framework)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var host = (AbsolutePath)Path.GetDirectoryName(projectPath) / "bin" / Configuration / framework / projectName;
        return OperatingSystem.IsWindows() ? host + ".exe" : host;
    }

    // ─── Per-lane environments ─────────────────────────────────────────

    /// <summary>
    /// Composes the per-lane environment providers a target asked for. Null when none apply, so
    /// the factory takes its default path.
    /// </summary>
    Func<WorkerLaunchContext, IReadOnlyDictionary<string, string>> laneEnvironment(
        int workers, bool postgres, bool sqlServer)
    {
        if (!postgres && !sqlServer) return null;

        var providers = new List<Func<WorkerLaunchContext, IReadOnlyDictionary<string, string>>>();
        if (postgres) providers.Add(postgresPerLane(workers));
        if (sqlServer) providers.Add(sqlServerPerLane(workers));

        if (providers.Count == 1) return providers[0];

        return context =>
        {
            var merged = new Dictionary<string, string>();
            foreach (var provider in providers)
            foreach (var pair in provider(context))
            {
                merged[pair.Key] = pair.Value;
            }

            return merged;
        };
    }

    /// <summary>
    /// One Postgres database per worker lane. Provisions wolverine_w0..N-1 on the docker-compose
    /// server up front, then points each lane's process at its own via WOLVERINE_POSTGRES.
    /// Discovery and isolated/recycled workers all report lane 0, so the databases provisioned
    /// equal the workers asked for, not the processes launched.
    /// </summary>
    Func<WorkerLaunchContext, IReadOnlyDictionary<string, string>> postgresPerLane(int workers)
    {
        ensurePostgresLaneDatabases(workers);

        return context => new Dictionary<string, string>
        {
            ["WOLVERINE_POSTGRES"] = postgresLaneConnectionString(context.Lane)
        };
    }

    /// <summary>
    /// One SQL Server database per worker lane, same shape as <see cref="postgresPerLane"/>:
    /// wolverine_w0..N-1 provisioned on the docker-compose server, each lane pointed at its own
    /// catalog via WOLVERINE_SQLSERVER. Schema names are hard-coded throughout the suites, so two
    /// processes sharing one catalog collide however the tests are partitioned — isolation has to
    /// be the database.
    /// </summary>
    Func<WorkerLaunchContext, IReadOnlyDictionary<string, string>> sqlServerPerLane(int workers)
    {
        ensureSqlServerLaneDatabases(workers);

        return context => new Dictionary<string, string>
        {
            ["WOLVERINE_SQLSERVER"] = sqlServerLaneConnectionString(context.Lane)
        };
    }

    static string sqlServerLaneConnectionString(int lane)
        => $"Server=localhost,1434;User Id=sa;Password=P@55w0rd;Timeout=5;MultipleActiveResultSets=True;Initial Catalog=wolverine_w{lane};Encrypt=False";

    void ensureSqlServerLaneDatabases(int workers)
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(SqlServerConnectionString);
        conn.Open();

        for (var lane = 0; lane < workers; lane++)
        {
            var database = $"wolverine_w{lane}";

            using var create = conn.CreateCommand();
            // Identifier, not a value — cannot be parameterized. The name is generated above,
            // never user input.
            create.CommandText = $"if DB_ID('{database}') is null begin CREATE DATABASE {database} end";
            create.ExecuteNonQuery();
        }
    }

    static string postgresLaneConnectionString(int lane)
        => $"Host=localhost;Port=5433;Database=wolverine_w{lane};Username=postgres;password=postgres";

    void ensurePostgresLaneDatabases(int workers)
    {
        // The admin connection targets the default database; CREATE DATABASE cannot run inside
        // a transaction or a pooled multiplexed connection, hence Pooling=false.
        using var conn = new Npgsql.NpgsqlConnection(PostgresConnectionString + ";Pooling=false");
        conn.Open();

        for (var lane = 0; lane < workers; lane++)
        {
            var database = $"wolverine_w{lane}";

            using var check = conn.CreateCommand();
            check.CommandText = "select 1 from pg_database where datname = @name";
            check.Parameters.AddWithValue("name", database);
            if (check.ExecuteScalar() is not null) continue;

            Log.Information("Provisioning per-lane database {Database}", database);
            using var create = conn.CreateCommand();
            // Identifier, not a value — cannot be parameterized. The name is generated above,
            // never user input.
            create.CommandText = $"CREATE DATABASE {database}";
            create.ExecuteNonQuery();
        }
    }
}
