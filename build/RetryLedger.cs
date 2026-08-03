using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Bobcat.Supervisor;
using Nuke.Common.IO;
using Serilog;

// The retry ledger: what every supervised run reports about its OWN flakiness, where somebody will
// actually see it. See GH-3787.
//
// The problem this exists for: CIAzureServiceBus was green for four consecutive main runs while
// spending 22 of its 25-retry budget on every one of them — the same 22 tests failing on the first
// attempt and passing alone in a fresh process, because the emulator wasn't warm yet. That was 85%
// of all flakiness in the repository, and nothing in the GitHub UI distinguishes a job at 22/25
// retries from one at 0/25. Both render as a green tick.
//
// Bobcat already logged all of it. Serilog warnings are not GitHub annotations, though — the
// annotations endpoint for that job returns an empty list — so reading them meant opening the log
// of a job that PASSED, which nobody has a reason to do. It was found by accident.
//
// Three outputs, deliberately:
//
//   - `$GITHUB_STEP_SUMMARY` markdown, so the numbers are on the run page without opening a log.
//   - a `::warning` workflow command when the count is nonzero, so it reaches the Annotations panel.
//   - a JSON ledger under artifacts/test-ledger/, uploaded per job and aggregated by the
//     `flakiness` roll-up job in tests.yml, which diffs the run against the last completed main run.
//
// The third is the one that matters most. The absolute count mattered less than the CHANGE in it:
// the step from 1 to 22 between two adjacent main runs was the real signal, and no baseline existed
// anywhere to notice it against.
//
// Deliberately NOT here: failing the build past a retry threshold. A suite legitimately sitting at
// 3 today would be one bad day away from a red main, and the value is in visibility, not a cliff.
partial class Build
{
    /// <summary>
    /// Per-project ledger files, one per project+framework, uploaded as a CI artifact. Under
    /// artifacts/ because <see cref="Clean"/> empties it at the start of every run, so a ledger can
    /// never be a stale leftover from a previous invocation.
    /// </summary>
    static AbsolutePath LedgerDirectory => RootDirectory / "artifacts" / "test-ledger";

    /// <summary>
    /// The CI job this run belongs to (e.g. CIAzureServiceBus). Set by tests.yml from the matrix
    /// target; the Nuke target name isn't reachable from here and GITHUB_JOB reports the matrix's
    /// job id ("test"), which is the same string for all thirty jobs.
    /// </summary>
    static string ledgerJobName => Environment.GetEnvironmentVariable("CI_JOB_NAME") is { Length: > 0 } name
        ? name
        : "local";

    void recordLedger(string projectName, string framework, SupervisorResults results)
    {
        var entry = new LedgerEntry
        {
            Job = ledgerJobName,
            Project = projectName,
            Framework = framework,
            Tests = results.Tests.Count,
            CleanPasses = results.CleanPasses.Count,
            PassedOnRetry = results.PassedOnRetry.Count,
            RetriesPerformed = results.RetriesPerformed,
            Failed = results.Failed.Count,
            Indeterminate = results.Indeterminate.Count,
            WorkerFaults = results.WorkerFaults.Count,
            AbortReason = results.AbortReason,
            // Bounded by the budget itself: the point of the list is to name the suspects, not to
            // reproduce the log.
            FlakyTests = results.PassedOnRetry.Select(x => x.DisplayName).Take(MaxRetriesPerRun).ToArray()
        };

        writeLedgerFile(entry);
        appendStepSummary(entry);
        annotate(entry);
    }

    static void writeLedgerFile(LedgerEntry entry)
    {
        try
        {
            LedgerDirectory.CreateDirectory();

            // One file per project+framework: a target can run several projects, and a project can
            // run under several TFMs, so neither alone is a unique key.
            var fileName = $"{entry.Job}.{entry.Project}.{entry.Framework}.json";
            var path = LedgerDirectory / fileName;

            File.WriteAllText(path, JsonSerializer.Serialize(entry, LedgerJson));
        }
        catch (Exception e)
        {
            // Reporting about the tests must never be what fails the tests.
            Log.Warning(e, "Could not write the retry ledger for {Project}", entry.Project);
        }
    }

    /// <summary>
    /// Appends this project's row to the job summary. Every supervised project in the job appends
    /// to the same file, so the header is written once, on the first append.
    /// </summary>
    static void appendStepSummary(LedgerEntry entry)
    {
        var summaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(summaryFile)) return;

        try
        {
            var builder = new StringBuilder();

            if (new FileInfo(summaryFile) is { Exists: false } or { Length: 0 })
            {
                builder.AppendLine($"### Retry ledger — {entry.Job}");
                builder.AppendLine();
                builder.AppendLine("| project | tests | clean | passed on retry | retries | failed | indeterminate |");
                builder.AppendLine("|---|--:|--:|--:|--:|--:|--:|");
            }

            builder.AppendLine(
                $"| {entry.Project} ({entry.Framework}) | {entry.Tests} | {entry.CleanPasses} | " +
                $"{mark(entry.PassedOnRetry)} | {mark(entry.RetriesPerformed)} | {mark(entry.Failed)} | " +
                $"{mark(entry.Indeterminate)} |");

            if (entry.AbortReason is not null)
            {
                builder.AppendLine();
                builder.AppendLine($"> **ABORTED** — {entry.AbortReason}");
            }

            if (entry.FlakyTests.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("<details><summary>Tests that only passed on a retry</summary>");
                builder.AppendLine();
                foreach (var test in entry.FlakyTests) builder.AppendLine($"- `{test}`");
                builder.AppendLine();
                builder.AppendLine("</details>");
            }

            builder.AppendLine();

            File.AppendAllText(summaryFile, builder.ToString());
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not append to $GITHUB_STEP_SUMMARY for {Project}", entry.Project);
        }

        // Zero reads as zero; anything else is bolded, because the eye is meant to stop on it.
        static string mark(int count) => count == 0 ? "0" : $"**{count}**";
    }

    /// <summary>
    /// Emits the retry count as a GitHub annotation. Serilog's warnings are not annotations — the
    /// annotations endpoint came back empty for the run that was burning 22 retries — so this has
    /// to be the literal workflow command on stdout.
    /// </summary>
    static void annotate(LedgerEntry entry)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        if (entry.RetriesPerformed == 0) return;

        var suspects = entry.FlakyTests.Length > 0
            ? $" First flaky test: {entry.FlakyTests[0]}."
            : "";

        // Workflow commands are newline-delimited, so the message has to be a single line.
        Console.WriteLine(
            $"::warning title={entry.Job}: {entry.RetriesPerformed} retries::" +
            $"{entry.Project} ({entry.Framework}) spent {entry.RetriesPerformed} of its " +
            $"{MaxRetriesPerRun}-retry budget; " +
            $"{entry.PassedOnRetry} test(s) passed only on a retry.{suspects}");
    }

    static readonly JsonSerializerOptions LedgerJson = new() { WriteIndented = true };

    /// <summary>
    /// One supervised project run, as the roll-up job consumes it. Property names are the contract
    /// with build/flakiness-report.sh — rename one and the jq there goes silently null, which is why
    /// that script asserts on the fields it reads.
    /// </summary>
    class LedgerEntry
    {
        public string Job { get; init; }
        public string Project { get; init; }
        public string Framework { get; init; }
        public int Tests { get; init; }
        public int CleanPasses { get; init; }
        public int PassedOnRetry { get; init; }
        public int RetriesPerformed { get; init; }
        public int Failed { get; init; }
        public int Indeterminate { get; init; }
        public int WorkerFaults { get; init; }
        public string AbortReason { get; init; }
        public string[] FlakyTests { get; init; } = [];
    }
}
