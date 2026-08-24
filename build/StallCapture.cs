using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Bobcat.Supervisor;
using Serilog;

// The successor to build/ci-memory-sampler.sh (GH-4083/GH-4089), retired when Bobcat 0.8.0 moved
// stall detection, RSS sampling and the pre-kill diagnostic seam inside the supervisor
// (JasperFx/bobcat#145-#150). What the shell watchdog had to approximate from outside the process
// — guessing which pid was the test host, inferring a stall from flat RSS and idle CPU — the
// supervisor now states as facts: which TEST is in flight past its budget, in which lane, in which
// pid. What stays here is the part Bobcat deliberately does not ship: the dotnet-dump capture
// (the consumer knows what it wants to capture and how long it can afford), and the cancellation
// handler that writes the partial ledger a capped job used to take to its grave.
//
// Output contract carried over from the sampler: diagnostics go to STDOUT prefixed `[stall]`,
// never inside a ::group:: — a cancelled job skips later steps, and an unclosed group hides its
// contents. Retrieval: `gh run view <id> --log | grep '\[stall\]'`.
partial class Build
{
    /// <summary>
    /// Invoked by the supervisor with a live worker immediately before it is forcibly killed —
    /// a worker that would not exit when asked, or one that never became usable. The useful
    /// artifact for a hung .NET process is the async "stack" on its GC heap, not any thread's
    /// stack: `dumpasync --coalesce` is what diagnosed the wedged Pulsar producers in GH-4100.
    /// </summary>
    static Task captureBeforeKill(WorkerKillContext context)
    {
        if (context.ProcessId is { } pid)
        {
            Console.WriteLine(
                $"[stall] a live worker (pid {pid}) is about to be killed — {context.Reason}. " +
                "Capturing its async stacks first.");
            captureAsyncStacks(pid);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// GH-4098 / JasperFx/bobcat#150: a job killed at the 20-minute cap used to report nothing —
    /// Bobcat printed its summary only at the end, GitHub discards a cancelled job's logs, and
    /// the flakiness roll-up called the job "unmeasured". Registers SIGTERM/SIGINT handlers for
    /// the duration of one supervised run: snapshot → partial ledger → name the stalled tests →
    /// best-effort async stacks → exit 2. The ledger write comes first because it is fast and
    /// must land inside whatever grace the runner grants; the dumps are gravy that may be cut
    /// short by the hard kill.
    /// </summary>
    IDisposable registerCancellationCapture(Supervisor supervisor, string projectName, string framework)
    {
        // Only under Actions: locally Ctrl-C should stay an ordinary Ctrl-C.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return null;

        var fired = 0;

        void handle(PosixSignalContext context)
        {
            context.Cancel = true;
            if (Interlocked.Exchange(ref fired, 1) != 0) return;

            Console.WriteLine(
                "[stall] cancellation signal received — writing the partial ledger before the runner " +
                "discards the run");

            try
            {
                var snapshot = supervisor.Snapshot();
                recordLedger(projectName, framework, snapshot);
                Log.Warning("=== {Project}: {Summary} ===", projectName, snapshot.Summarize());

                foreach (var stalled in snapshot.StalledTests)
                {
                    Console.WriteLine(
                        $"[stall] stalled at cancellation: {stalled.DisplayName} " +
                        $"({(int)stalled.InFlight.TotalSeconds}s in flight, lane {stalled.Worker.Lane}, " +
                        $"pid {stalled.Worker.ProcessId?.ToString() ?? "unknown"})");
                }

                foreach (var stalled in snapshot.StalledTests)
                {
                    if (stalled.Worker.ProcessId is { } pid) captureAsyncStacks(pid);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[stall] cancellation capture failed: {e.Message}");
            }

            Environment.Exit(2);
        }

        return new SignalRegistrations(
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, handle),
            PosixSignalRegistration.Create(PosixSignal.SIGINT, handle));
    }

    sealed class SignalRegistrations(params IDisposable[] registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }

    // ─── The dotnet-dump pipeline, ported from the sampler's async_stacks() ───

    /// <summary>Lines of `dumpasync --coalesce` output kept — same bound the sampler used.</summary>
    const int StallDumpLines = 400;

    static void captureAsyncStacks(int pid)
    {
        try
        {
            var tool = ensureDotnetDump();
            if (tool is null)
            {
                Console.WriteLine("[stall] dotnet-dump is unavailable and could not be installed — no capture");
                return;
            }

            var dump = Path.Combine(Path.GetTempPath(), $"wolverine-stall-{pid}.dmp");

            if (!runTool(tool, $"collect -p {pid} -o {dump}", input: null,
                    TimeSpan.FromSeconds(240), out var collectOutput))
            {
                Console.WriteLine($"[stall] dotnet-dump collect failed for pid {pid}: {condense(collectOutput)}");
                return;
            }

            if (!runTool(tool, $"analyze {dump}", input: "dumpasync --coalesce\nexit\n",
                    TimeSpan.FromSeconds(300), out var stacks))
            {
                Console.WriteLine($"[stall] dumpasync failed for pid {pid}: {condense(stacks)}");
            }
            else
            {
                Console.WriteLine($"[stall] === async stacks of pid {pid} (dumpasync --coalesce) ===");
                var lines = stacks.Split('\n');
                foreach (var line in lines.AsSpan(0, Math.Min(lines.Length, StallDumpLines)))
                {
                    Console.WriteLine($"[stall] {line.TrimEnd()}");
                }

                if (lines.Length > StallDumpLines)
                    Console.WriteLine($"[stall] … {lines.Length - StallDumpLines} more line(s) elided");
            }

            try { File.Delete(dump); } catch { /* tmp cleanup only */ }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[stall] async-stack capture for pid {pid} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Finds dotnet-dump, installing it as a global tool on first use — the sampler did the
    /// same, lazily, so a run that never wedges never pays for it.
    /// </summary>
    static string ensureDotnetDump()
    {
        if (runTool("dotnet-dump", "--version", null, TimeSpan.FromSeconds(10), out _)) return "dotnet-dump";

        runTool("dotnet", "tool install -g dotnet-dump", null, TimeSpan.FromSeconds(120), out _);

        if (runTool("dotnet-dump", "--version", null, TimeSpan.FromSeconds(10), out _)) return "dotnet-dump";

        // Fresh installs land in ~/.dotnet/tools, which may not be on this process's PATH.
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
            OperatingSystem.IsWindows() ? "dotnet-dump.exe" : "dotnet-dump");

        return File.Exists(installed) ? installed : null;
    }

    /// <summary>Runs one bounded external command; overrunning the budget kills the tree.</summary>
    static bool runTool(string fileName, string arguments, string input, TimeSpan budget, out string output)
    {
        output = "";
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input is not null,
                UseShellExecute = false
            };

            if (!process.Start()) return false;

            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)budget.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                output = "timed out";
                return false;
            }

            output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception e)
        {
            output = e.Message;
            return false;
        }
    }
}
