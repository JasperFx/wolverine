using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Nuke.Common;
using Nuke.Common.Tooling;
using Serilog;

// GH-5532. A run that OWNS SQL Server or the Azure Service Bus emulator starts it fresh: the containers
// are recreated with new anonymous volumes and waited on until they answer, before the run's first test.
//
// Why: both pile up state on a box where they live for days. The ASB emulator's SQL backend slowed the
// suite to ~2 minutes per test (20 tests in 30 seconds once recreated), and a 5-day-old wolverine-sqlserver-1
// with 25 databases and 313 schemas sat at 328% CPU waiting on RESOURCE_SEMAPHORE_QUERY_COMPILE.
//
// When: ONLY at the start of a run that owns the service (StartServicesFresh's callers: the CISqlServer /
// CIPolecat / CIAzureServiceBus shard runners, TestAllPersistence / TestAllTransports via FreshContainers,
// and LocalGateSuites). Never mid-run, and never from DockerUp or a plain `dotnet test`: every worktree and
// every Stoat worker on the box shares the wolverine-* containers, and recreating SQL Server under someone
// else's running SqlServerTests fails them.
//
// Safety, in order:
//   1. --no-fresh-containers (or WOLVERINE_NO_FRESH_CONTAINERS=1) turns it off: the services are started
//      or reused exactly as before.
//   2. Once per service per build process.
//   3. A cross-process file lock per service under ~/.wolverine/locks, held through the recreate and the
//      readiness wait, so two runs on one box cannot recreate the same service at once. The lock file
//      records when the service was last started fresh; a run that gets the lock within FreshWindow of
//      that reuses the service rather than recreating it again.
//   4. A container that is not this compose project's (another repository's SQL Server holding 1434, say)
//      is never touched.
//   5. A service someone is using right now is never recreated: SQL Server with another client's session
//      open, or the emulator with a Wolverine.AzureServiceBus.Tests process running. The run then reuses
//      it, with a warning. Skipping is never a failure.
partial class Build
{
    [Parameter("GH-5532: do not recreate SQL Server / the Azure Service Bus emulator fresh at the start of a run " +
               "that owns them; start or reuse them as they are (also WOLVERINE_NO_FRESH_CONTAINERS=1)")]
    readonly bool NoFreshContainers;

    /// <summary>A service started fresh this recently, by any run on the box, is reused rather than recreated.</summary>
    static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long a run waits for another run's fresh start of the same service to finish.</summary>
    static readonly TimeSpan FreshLockBudget = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The services a run may start fresh, keyed by the compose service a target asks for. Each recreates
    /// as a group: the emulators' SQL backends are what pile up the state, and <c>depends_on</c> alone
    /// does not recreate them.
    /// </summary>
    static readonly (string Requested, string Lock, string[] Recreate, string[] Await)[] FreshServices =
    [
        ("sqlserver", "sqlserver", ["sqlserver"], ["sqlserver"]),
        ("asb-emulator", "asb", ["asb-sql", "asb-emulator", "asb-sql-2", "asb-emulator-2"], ["asb-emulator", "asb-emulator-2"])
    ];

    static readonly HashSet<string> _startedFresh = new(StringComparer.OrdinalIgnoreCase);

    bool freshContainersDisabled =>
        NoFreshContainers ||
        Environment.GetEnvironmentVariable("WOLVERINE_NO_FRESH_CONTAINERS") is "1" or "true" or "True" or "TRUE";

    /// <summary>
    /// Wired into TestAllPersistence (SQL Server) and TestAllTransports (the ASB emulator), before
    /// DockerUp, which itself never starts anything fresh: plenty of targets that do not own these
    /// services depend on it.
    /// </summary>
    Target FreshContainers => _ => _
        .Unlisted()
        .Before(DockerUp)
        .Executes(() =>
        {
            var services = new List<string>();
            if (ExecutionPlan.Any(x => x.Name == nameof(TestAllPersistence))) services.Add("sqlserver");
            if (ExecutionPlan.Any(x => x.Name == nameof(TestAllTransports))) services.Add("asb-emulator");
            StartServicesFresh(services.ToArray());
        });

    /// <summary>
    /// Starts the services and waits until they answer, recreating SQL Server and the ASB emulator pairs
    /// fresh first where this run may (see the top of this file). Anything else in the list is started or
    /// reused exactly as <see cref="StartDockerServices"/> does. Call it only at the start of a run that
    /// owns the services.
    /// </summary>
    void StartServicesFresh(params string[] services)
    {
        var rest = new List<string>(services);

        foreach (var fresh in FreshServices.Where(f => services.Contains(f.Requested)))
        {
            rest.Remove(fresh.Requested);
            if (!startFresh(fresh.Lock, fresh.Recreate, fresh.Await))
                StartDockerServices(fresh.Requested);
        }

        if (rest.Count > 0) StartDockerServices(rest.ToArray());
    }

    /// <summary>True when it recreated the services and they answer; false when the caller should start or reuse them as usual.</summary>
    bool startFresh(string name, string[] recreate, string[] awaitServices)
    {
        if (freshContainersDisabled)
        {
            Log.Information("Fresh containers are off (--no-fresh-containers / WOLVERINE_NO_FRESH_CONTAINERS): reusing {Service}", name);
            return false;
        }

        if (!_startedFresh.Add(name)) return false;

        using var freshLock = acquireFreshLock(name);

        if (freshLock.LastFresh is { } last && DateTimeOffset.UtcNow - last < FreshWindow)
        {
            Log.Information("{Service} was started fresh {Ago} ago by a run on this box; reusing it",
                name, humanize(DateTimeOffset.UtcNow - last));
            return false;
        }

        if (whyNotFresh(name, recreate) is { } reason)
        {
            Log.Warning("Not starting {Service} fresh: {Reason}. Reusing it as it is", name, reason);
            return false;
        }

        var list = string.Join(" ", recreate);
        Log.Information("Starting {Services} fresh: recreating the containers with new volumes (GH-5532)", list);

        // This process's own pooled connections point at the container about to go away.
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        ComposeUp(composeArgs("up -d --force-recreate --renew-anon-volumes " + list), list);
        AwaitDockerServices(awaitServices);

        freshLock.MarkFresh();
        Log.Information("{Services} started fresh and answering", list);
        return true;
    }

    /// <summary>Null when the services may be recreated, otherwise why not.</summary>
    string whyNotFresh(string name, string[] recreate)
    {
        var ports = composeServicePorts();
        foreach (var service in recreate)
        {
            if (!ports.TryGetValue(service, out var port) || !portAnswers(port)) continue;
            if (!composeProjectHas(service))
                return $"port {port} is held by a container that is not compose project '{ComposeProjectName}''s {service}";
        }

        return name switch
        {
            "sqlserver" => sqlServerSessionsInUse(),
            "asb" => otherProcessRunning("Wolverine.AzureServiceBus.Tests"),
            _ => null
        };
    }

    static bool composeProjectHas(string service)
    {
        try
        {
            var process = ProcessTasks
                .StartProcess(composeTool(), composeArgs($"ps -q {service}"), logOutput: false, logInvocation: false)
                .AssertWaitForExit();
            return process.ExitCode == 0 && process.Output.Any(x => !string.IsNullOrWhiteSpace(x.Text));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Another client's session on the shared SQL Server means a run is using it: a test host keeps its
    /// pooled connections open for its whole life. This process's own sessions do not count.
    /// </summary>
    string sqlServerSessionsInUse()
    {
        if (!portAnswers(1434)) return null;

        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(
                new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerConnectionString) { Pooling = false }
                    .ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "select count(*), count(distinct host_process_id) from sys.dm_exec_sessions " +
                "where is_user_process = 1 and session_id <> @@SPID and isnull(host_process_id, 0) <> @pid";
            cmd.Parameters.AddWithValue("@pid", Environment.ProcessId);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            var sessions = reader.GetInt32(0);
            return sessions == 0
                ? null
                : $"{sessions} session(s) from {reader.GetInt32(1)} other client process(es) are connected to it";
        }
        catch (Exception e)
        {
            // Better to leave a server alone than to recreate one we could not inspect.
            return $"could not check it for other sessions ({e.Message})";
        }
    }

    /// <summary>Another process on the box whose command line names the test host.</summary>
    static string otherProcessRunning(string testHost)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var others = Process.GetProcesses()
                    .Where(p => p.Id != Environment.ProcessId &&
                                p.ProcessName.Contains(testHost, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return others.Length == 0 ? null : $"{others.Length} {testHost} process(es) are running";
            }

            var pgrep = ProcessTasks.StartProcess("pgrep", $"-f {testHost}", logOutput: false, logInvocation: false)
                .AssertWaitForExit();
            var pids = pgrep.Output.Select(x => x.Text.Trim())
                .Where(x => int.TryParse(x, out var pid) && pid != Environment.ProcessId)
                .ToArray();
            return pids.Length == 0 ? null : $"{testHost} is running (pid {string.Join(", ", pids)})";
        }
        catch (Exception e)
        {
            return $"could not check for running {testHost} processes ({e.Message})";
        }
    }

    static FreshLock acquireFreshLock(string name)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wolverine", "locks");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"fresh-{name}.lock");

        var clock = Stopwatch.StartNew();
        var logged = false;
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive flock on Unix and a share-mode lock on Windows; either way
                // the OS drops it when the process dies, so a killed build never leaves the lock held.
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new FreshLock(stream);
            }
            catch (IOException) when (clock.Elapsed < FreshLockBudget)
            {
                if (!logged)
                {
                    Log.Information("Another run on this box is starting {Service} fresh ({Path}); waiting for it", name, path);
                    logged = true;
                }

                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>The held lock file, which also records when its service was last started fresh.</summary>
    sealed class FreshLock(FileStream stream) : IDisposable
    {
        public DateTimeOffset? LastFresh { get; } = read(stream);

        static DateTimeOffset? read(FileStream stream)
        {
            stream.Position = 0;
            var text = new StreamReader(stream, leaveOpen: true).ReadToEnd().Trim();
            return DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
        }

        public void MarkFresh()
        {
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(DateTimeOffset.UtcNow.ToString("O"));
        }

        public void Dispose() => stream.Dispose();
    }
}
