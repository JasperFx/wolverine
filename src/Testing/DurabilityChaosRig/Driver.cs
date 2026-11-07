using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Npgsql;
using Weasel.Postgresql;
using Weasel.SqlServer;

namespace DurabilityChaosRig;

/// <summary>
/// Runs the workers and the producer as child processes, injects faults at random for a while, stops producing,
/// waits for the cluster to settle and then checks the ledger. Every fault is drawn from one seed and written to
/// schedule.log in the run directory.
/// </summary>
/// <remarks>
/// Faults, weighted by CHAOS_FAULTS (default "kill:6,term:2,pause:2,dbdown:1,brokerdown:1"):
/// kill = SIGKILL a process; term = SIGTERM (graceful stop); pause = SIGSTOP a worker past the stale node timeout,
/// then SIGCONT (it wakes up after its agents moved); dbdown / brokerdown = docker pause the database or the broker
/// container for a few seconds. With CHAOS_POISON set, a share of messages fails until the driver "ships the fix"
/// part way through, and the driver keeps marking dead letters replayable from then on.
/// </remarks>
public class Driver
{
    private readonly string _dll = typeof(Driver).Assembly.Location;
    private readonly string _dir;
    private readonly Dictionary<string, Process> _processes = new();

    // The dotnet process under each wrapper shell. Signals go to it rather than to the wrapper: .NET's
    // child-process tracking spins once a DIRECT child is SIGSTOPped, which stalls the driver itself
    private readonly Dictionary<string, int> _pids = new();
    private readonly int _workers;
    private readonly TimeSpan _chaos;
    private readonly Random _random;
    private readonly int _seed;
    private readonly List<(string fault, int weight)> _faults;
    private readonly StreamWriter _schedule;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _poisonFixed;

    public Driver(string dir, int workers, TimeSpan chaos, int seed)
    {
        _dir = dir;
        _workers = workers;
        _chaos = chaos;
        _seed = seed;
        _random = new Random(seed);
        Directory.CreateDirectory(_dir);
        _schedule = new StreamWriter(Path.Combine(_dir, "schedule.log")) { AutoFlush = true };

        var spec = Environment.GetEnvironmentVariable("CHAOS_FAULTS") ?? "kill:6,term:2,pause:2,dbdown:1,brokerdown:1";
        _faults = spec.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':'))
            .Select(x => (x[0], int.Parse(x[1])))
            .Where(x => x.Item2 > 0)
            .Where(x => x.Item1 != "brokerdown" || Node.Transport == "rabbit")
            .ToList();
    }

    private static string DatabaseContainer => Node.Store == "ef" ? "wolverine-sqlserver-1" : "wolverine-postgresql-1";
    private static bool Poison => Environment.GetEnvironmentVariable("CHAOS_POISON") is { Length: > 0 } p && p != "0";

    private void log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    private void schedule(string message)
    {
        log(message);
        _schedule.WriteLine($"{_clock.Elapsed.TotalSeconds:F1}s {message}");
    }

    public async Task<int> RunAsync()
    {
        File.Delete(Path.Combine(_dir, "stop-producing"));
        File.Delete(Node.PoisonFixedFile);
        schedule($"seed {_seed}, store {Node.Store}, transport {Node.Transport}, schema {Node.Schema}, {_workers} workers, " +
                 $"chaos for {_chaos}, faults [{string.Join(", ", _faults.Select(x => $"{x.fault}:{x.weight}"))}], " +
                 $"poison {Environment.GetEnvironmentVariable("CHAOS_POISON") ?? "off"}, short timeouts " +
                 $"{Environment.GetEnvironmentVariable("CHAOS_SHORT_TIMEOUTS") ?? "off"}, logs in {_dir}");

        await resetAsync();

        for (var i = 1; i <= _workers; i++) start($"w{i}", "worker");
        await Task.Delay(TimeSpan.FromSeconds(10));
        start("producer", "producer");

        var counts = new Dictionary<string, int>();
        var started = DateTime.UtcNow;
        var ending = started.Add(_chaos);
        while (DateTime.UtcNow < ending)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(2000, 7000)));

            if (Poison && !_poisonFixed && DateTime.UtcNow - started > _chaos * 0.4) await shipThePoisonFixAsync();
            if (_poisonFixed) await replayDeadLettersAsync();

            if (_faults.Count == 0) continue;

            var fault = pickFault();
            counts[fault] = counts.GetValueOrDefault(fault) + 1;
            await injectAsync(fault);
        }

        schedule($"chaos over: {string.Join(", ", counts.Select(x => $"{x.Value} {x.Key}"))}. Stopping the producer");
        if (Poison && !_poisonFixed) await shipThePoisonFixAsync();

        await File.WriteAllTextAsync(Path.Combine(_dir, "stop-producing"), "");
        if (!_processes["producer"].WaitForExit(TimeSpan.FromSeconds(30))) _processes["producer"].Kill(true);

        // Anything a fault left paused has to be running again before the cluster can settle
        foreach (var name in _processes.Keys) signal(name, "CONT");

        var report = await settleAsync();

        foreach (var process in _processes.Values.Where(x => !x.HasExited))
        {
            process.Kill(true);
        }

        schedule(report.ToString());
        return report.Clean ? 0 : 1;
    }

    private string pickFault()
    {
        var roll = _random.Next(0, _faults.Sum(x => x.weight));
        foreach (var (fault, weight) in _faults)
        {
            if (roll < weight) return fault;
            roll -= weight;
        }

        return _faults[^1].fault;
    }

    private async Task injectAsync(string fault)
    {
        // The producer is a fifth of the process targets, so outbox recovery gets its share
        var name = _random.NextDouble() < 0.2 ? "producer" : $"w{_random.Next(1, _workers + 1)}";
        var role = name == "producer" ? "producer" : "worker";

        switch (fault)
        {
            case "kill":
                schedule($"SIGKILL {name}");
                _processes[name].Kill(entireProcessTree: true);
                await _processes[name].WaitForExitAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(300, 3000)));
                start(name, role);
                break;

            case "term":
                schedule($"SIGTERM {name}");
                await terminateAsync(name);
                await Task.Delay(TimeSpan.FromMilliseconds(_random.Next(300, 3000)));
                start(name, role);
                break;

            case "pause":
            {
                // Past the 10 second stale node timeout, so the cluster gives its agents away while it sleeps
                var worker = $"w{_random.Next(1, _workers + 1)}";
                var seconds = _random.Next(12, 22);
                schedule($"SIGSTOP {worker} for {seconds}s");
                signal(worker, "STOP");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                signal(worker, "CONT");
                schedule($"SIGCONT {worker}");
                break;
            }

            case "dbdown":
            {
                var seconds = _random.Next(3, 12);
                schedule($"docker pause {DatabaseContainer} for {seconds}s");
                await dockerAsync($"pause {DatabaseContainer}");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await dockerAsync($"unpause {DatabaseContainer}");
                schedule($"docker unpause {DatabaseContainer}");
                break;
            }

            case "brokerdown":
            {
                var seconds = _random.Next(3, 12);
                schedule($"docker pause wolverine-rabbitmq-1 for {seconds}s");
                await dockerAsync("pause wolverine-rabbitmq-1");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                await dockerAsync("unpause wolverine-rabbitmq-1");
                schedule("docker unpause wolverine-rabbitmq-1");
                break;
            }
        }
    }

    private async Task shipThePoisonFixAsync()
    {
        schedule("shipping the poison fix; dead letters are replayed from now on");
        await File.WriteAllTextAsync(Node.PoisonFixedFile, "");
        _poisonFixed = true;
    }

    private async Task replayDeadLettersAsync()
    {
        try
        {
            await using var conn = openConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Node.Store == "ef"
                ? $"update {Node.Schema}.wolverine_dead_letters set replayable = 1 where replayable = 0"
                : $"update {Node.Schema}.wolverine_dead_letters set replayable = true where replayable = false";
            var marked = await cmd.ExecuteNonQueryAsync();
            if (marked > 0) schedule($"marked {marked} dead letter(s) replayable");
        }
        catch (Exception e)
        {
            log($"replay failed (database down?): {e.Message}");
        }
    }

    private void signal(string name, string signal)
    {
        if (_processes[name].HasExited) return;
        Process.Start("kill", $"-{signal} {_pids[name]}")!.WaitForExit();
    }

    private static async Task dockerAsync(string args)
    {
        var docker = Process.Start(new ProcessStartInfo("docker", args)
            { RedirectStandardOutput = true, RedirectStandardError = true })!;
        await docker.WaitForExitAsync();
    }

    private void start(string name, string role)
    {
        // "; true" keeps sh from exec-ing dotnet in its place, so dotnet stays a grandchild
        var info = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", $"dotnet \"{_dll}\" {role}; true" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.Environment["CHAOS_NODE"] = name;
        info.Environment["CHAOS_DIR"] = _dir;

        var process = Process.Start(info)!;
        var file = new StreamWriter(Path.Combine(_dir, $"{name}.log"), append: true) { AutoFlush = true };
        file.WriteLine($"===== {DateTime.Now:HH:mm:ss.fff} started pid {process.Id}");
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (file) file.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (file) file.WriteLine("ERR " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes[name] = process;
        _pids[name] = findChild(process.Id);
    }

    private static int findChild(int parent)
    {
        for (var i = 0; i < 100; i++)
        {
            var pgrep = Process.Start(new ProcessStartInfo("pgrep", $"-P {parent}") { RedirectStandardOutput = true })!;
            var output = pgrep.StandardOutput.ReadToEnd().Trim();
            pgrep.WaitForExit();
            if (int.TryParse(output.Split('\n')[0], out var pid)) return pid;
            Thread.Sleep(20);
        }

        throw new InvalidOperationException($"No dotnet process appeared under the wrapper shell {parent}");
    }

    private async Task terminateAsync(string name)
    {
        var process = _processes[name];
        signal(name, "TERM");
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            schedule($"{name} did not stop within 30s of SIGTERM, killing it");
            process.Kill(true);
        }

        await process.WaitForExitAsync();
    }

    internal static DbConnection openConnection() => Node.Store == "ef"
        ? new SqlConnection(Node.SqlServerConnectionString)
        : new NpgsqlConnection(Node.PostgresConnectionString);

    private async Task resetAsync()
    {
        foreach (var schema in new[] { Node.Schema, Node.Schema + "_queues" })
        {
            if (Node.Store == "ef")
            {
                await using var conn = new SqlConnection(Node.SqlServerConnectionString);
                await conn.OpenAsync();
                await conn.DropSchemaAsync(schema);
            }
            else
            {
                await using var conn = new NpgsqlConnection(Node.PostgresConnectionString);
                await conn.OpenAsync();
                await conn.DropSchemaAsync(schema);
            }
        }

        if (Node.Transport == "rabbit")
        {
            // Drop the work queue and empty the native dead letter queue, so a previous run cannot leak in
            await dockerAsync("exec wolverine-rabbitmq-1 rabbitmqctl delete_queue chaos-work");
            await dockerAsync("exec wolverine-rabbitmq-1 rabbitmqctl purge_queue wolverine-dead-letter-queue");
        }

        log("reset the database schemas");
    }

    /// <summary>
    /// Wait until the ledger is complete, or has made no progress for three minutes
    /// </summary>
    private async Task<Report> settleAsync()
    {
        var last = await Report.ReadAsync();
        var lastProgress = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (_poisonFixed) await replayDeadLettersAsync();

            var report = await Report.ReadAsync();
            log($"settling: {report.Summary}");

            if (report.Clean) return report;

            if (report.Summary != last.Summary)
            {
                lastProgress = DateTime.UtcNow;
                last = report;
            }
            else if (DateTime.UtcNow - lastProgress > TimeSpan.FromMinutes(3))
            {
                return report;
            }
        }
    }
}

public class Report
{
    public long Sent { get; private set; }
    public long LostWork { get; private set; }
    public long LostLocal { get; private set; }
    public long LostDelayed { get; private set; }
    public long Duplicates { get; private set; }
    public long Incoming { get; private set; }
    public long Scheduled { get; private set; }
    public long Outgoing { get; private set; }
    public long DeadLetters { get; private set; }
    public long RabbitDeadLetters { get; private set; }
    public string Latency { get; private set; } = "";
    public List<string> Details { get; } = new();

    public bool Clean => LostWork == 0 && LostLocal == 0 && LostDelayed == 0 && Duplicates == 0 && Incoming == 0 &&
                         Scheduled == 0 && Outgoing == 0 && DeadLetters == 0 && RabbitDeadLetters == 0;

    public string Summary =>
        $"sent {Sent}, lost work {LostWork}, lost local {LostLocal}, lost delayed {LostDelayed}, duplicates {Duplicates}, " +
        $"inbox incoming {Incoming}, inbox scheduled {Scheduled}, outbox {Outgoing}, dead letters {DeadLetters}" +
        (Node.Transport == "rabbit" ? $", rabbit DLQ {RabbitDeadLetters}" : "");

    public override string ToString() =>
        (Clean ? "CLEAN: " : "PROBLEMS: ") + Summary + Environment.NewLine + "  latency: " + Latency +
        (Details.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Details));

    private static bool ef => Node.Store == "ef";
    private static string sentTable => ef ? $"{Node.Schema}.sent" : $"{Node.Schema}.mt_doc_sent";
    private static string processedTable => ef ? $"{Node.Schema}.processed" : $"{Node.Schema}.mt_doc_processed";
    private static string sentAt => ef ? "sent.at" : "(sent.data->>'At')::timestamptz";
    private static string processedAt => ef ? "p.at" : "(p.data->>'At')::timestamptz";
    private static string nodeAndAttempts => ef
        ? "concat(node, '@', attempts)"
        : "coalesce(data->>'Node', data->>'node') || '@' || coalesce(data->>'Attempts', data->>'attempts')";

    public static async Task<Report> ReadAsync()
    {
        var s = Node.Schema;
        var report = new Report();
        try
        {
            await using var conn = Driver.openConnection();
            await conn.OpenAsync();

            report.Sent = await scalar(conn, $"select count(*) from {sentTable}");
            report.LostWork = await scalar(conn, lost("work", 0));
            report.LostLocal = await scalar(conn, lost("local", Flags.Local));
            report.LostDelayed = await scalar(conn, lost("delayed", Flags.Delayed));
            report.Duplicates = await scalar(conn,
                $"select count(*) from (select message_id, stage from {processedTable} group by message_id, stage having count(*) > 1) d");
            report.Incoming = await scalar(conn, $"select count(*) from {s}.wolverine_incoming_envelopes where status = 'Incoming'");
            report.Scheduled = await scalar(conn, $"select count(*) from {s}.wolverine_incoming_envelopes where status = 'Scheduled'");
            report.Outgoing = await scalar(conn, $"select count(*) from {s}.wolverine_outgoing_envelopes");
            report.DeadLetters = await scalar(conn, $"select count(*) from {s}.wolverine_dead_letters");
            report.RabbitDeadLetters = Node.Transport == "rabbit" ? await rabbitDeadLettersAsync() : 0;

            if (!report.Clean)
            {
                await detailsAsync(conn, report, s);
            }
            else
            {
                report.Latency = await latencyAsync(conn);
            }
        }
        catch (Exception e)
        {
            // The database may be paused by a fault; report "no progress" rather than crash the driver
            report.Details.Add($"  could not read the ledger: {e.GetType().Name}: {e.Message}");
            report.LostWork = -1;
        }

        return report;
    }

    private static async Task<long> scalar(DbConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }
        catch (DbException e) when (e.Message.Contains("does not exist") || e.Message.Contains("Invalid object name"))
        {
            return 0; // not created yet
        }
    }

    private static async Task<long> rabbitDeadLettersAsync()
    {
        var info = new ProcessStartInfo("docker",
            "exec wolverine-rabbitmq-1 rabbitmqctl list_queues name messages") { RedirectStandardOutput = true };
        var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var line = output.Split('\n').FirstOrDefault(x => x.StartsWith("wolverine-dead-letter-queue"));
        return line != null && long.TryParse(line.Split('\t').Last().Trim(), out var count) ? count : 0;
    }

    private static string lost(string stage, int flag) =>
        $"select count(*) from {sentTable} sent where {(flag == 0 ? "1 = 1" : $"(sent.flags & {flag}) <> 0")} " +
        $"and not exists (select 1 from {processedTable} p where p.message_id = sent.id and p.stage = '{stage}')";

    /// <summary>
    /// How long the slowest messages took from the producer's commit to their handler's commit, per stage
    /// </summary>
    private static async Task<string> latencyAsync(DbConnection conn)
    {
        var seconds = ef
            ? "datediff_big(millisecond, sent.at, p.at) / 1000.0"
            : $"extract(epoch from ({processedAt} - {sentAt}))";
        var results = new List<string>();
        foreach (var stage in new[] { "work", "local", "delayed" })
        {
            var max = await scalarText(conn,
                $"select max({seconds}) from {sentTable} sent join {processedTable} p on p.message_id = sent.id and p.stage = '{stage}'");
            results.Add($"{stage} max {max}s");
        }

        return string.Join(", ", results);
    }

    private static async Task<string> scalarText(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? "-" : Convert.ToDouble(value).ToString("F1");
    }

    private static async Task detailsAsync(DbConnection conn, Report report, string s)
    {
        async Task rows(string title, string sql)
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync();
                var count = 0;
                while (await reader.ReadAsync() && count++ < 15)
                {
                    var values = new object[reader.FieldCount];
                    reader.GetValues(values);
                    report.Details.Add($"  {title}: {string.Join(" | ", values)}");
                }
            }
            catch (DbException)
            {
            }
        }

        await rows("lost", $"select sent.id, sent.flags, st.stage from {sentTable} sent cross join (values ('work', 0), ('local', {Flags.Local}), ('delayed', {Flags.Delayed})) st(stage, flag) " +
                           $"where (st.flag = 0 or (sent.flags & st.flag) <> 0) and not exists (select 1 from {processedTable} p where p.message_id = sent.id and p.stage = st.stage)");
        await rows("duplicate", $"select message_id, stage, count(*), string_agg({nodeAndAttempts}, ',') from {processedTable} group by message_id, stage having count(*) > 1");
        await rows("inbox", $"select id, status, owner_id, attempts, message_type, received_at, execution_time from {s}.wolverine_incoming_envelopes where status <> 'Handled'");
        await rows("outbox", $"select id, owner_id, destination, attempts, message_type from {s}.wolverine_outgoing_envelopes");
        await rows("dead letter", $"select id, message_type, exception_type, exception_message, replayable from {s}.wolverine_dead_letters");
    }
}
