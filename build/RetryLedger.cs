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
            FlakyTests = results.PassedOnRetry.Select(x => x.DisplayName).Take(MaxRetriesPerRun).ToArray(),
            FlakyFailures = results.PassedOnRetry.Take(MaxRetriesPerRun).Select(firstFailure).ToArray(),
            // The observability cluster's facts (Bobcat 0.8.0). IsPartial marks a ledger written
            // by the cancellation handler — the run so far, not a verdict; StalledTests carries
            // the names a capped job's log used to take to its grave (GH-4083); PeakWorkerRssMb
            // is the GH-4089 figure, per job instead of per shell-sampler scrape.
            IsPartial = results.IsPartial,
            Stalled = results.StalledTests.Count,
            StalledTests = results.StalledTests.Select(x => x.DisplayName).Distinct()
                .Take(MaxRetriesPerRun).ToArray(),
            PeakWorkerRssMb = RunResources.For(results).PeakBytes is { } peak ? peak / (1024 * 1024) : null
        };

        writeLedgerFile(entry);
        appendStepSummary(entry);
        annotate(entry);
    }

    /// <summary>
    /// GH-3855. Records that a readiness gate had to restart a container before the service came up.
    ///
    /// <para>Kept in the ledger rather than only in the job log, because the whole point of the
    /// restart is that it makes the job PASS. A silent recovery turns a degrading runner or a bad
    /// image push into invisible latency, and the ledger is the one surface here that diffs a run
    /// against the previous main run — which is what makes "this started happening" visible at all.
    /// The same rule the fresh-process retry already states: a pass on a retry is reported as flaky,
    /// never as clean.</para>
    ///
    /// <para>Written as its own entry with zero tests rather than folded into a project's row: this
    /// happened <i>above</i> the test runner, before a single test existed to attribute it to, and
    /// inflating CleanPasses/Tests to carry it would corrupt the columns that do mean tests.</para>
    /// </summary>
    void recordServiceRestart(string service, string composeService, int attemptsBefore, string lastReason,
        int attemptsAfter)
    {
        var entry = new LedgerEntry
        {
            Job = ledgerJobName,
            // Not a project. Named for what it is so a reader of the raw ledger is not left looking
            // for a test project by this name -- and kept to characters that are safe in a file
            // name, because these two fields compose one. "n/a" for the framework was the first
            // version of this and its slash sent the write into a directory that does not exist.
            Project = $"readiness-gate-{composeService}",
            Framework = "gate",
            ServiceRestarts =
            [
                new ServiceRestart
                {
                    Service = service,
                    ComposeService = composeService,
                    AttemptsBeforeRestart = attemptsBefore,
                    AttemptsAfterRestart = attemptsAfter,
                    Reason = condense(lastReason)
                }
            ]
        };

        writeLedgerFile(entry);
        appendRestartToStepSummary(entry.ServiceRestarts[0]);
        annotateRestart(entry.Job, entry.ServiceRestarts[0]);
    }

    static void appendRestartToStepSummary(ServiceRestart restart)
    {
        var summaryFile = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(summaryFile)) return;

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine(
                $"> **Restarted `{restart.ComposeService}`** — {restart.Service} was not ready after " +
                $"{restart.AttemptsBeforeRestart} attempts, came up {restart.AttemptsAfterRestart} " +
                $"attempt(s) after a restart. This job is NOT a clean start.");

            if (restart.Reason is not null)
            {
                builder.AppendLine("> ");
                builder.AppendLine($"> Last failure before the restart: `{restart.Reason}`");
            }

            builder.AppendLine();

            File.AppendAllText(summaryFile, builder.ToString());
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not append the {Service} restart to $GITHUB_STEP_SUMMARY", restart.Service);
        }
    }

    static void annotateRestart(string job, ServiceRestart restart)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;

        Console.WriteLine(
            $"::warning title={job}: restarted {restart.ComposeService}::" +
            $"{restart.Service} was not ready after {restart.AttemptsBeforeRestart} attempts and was " +
            $"restarted; it came up {restart.AttemptsAfterRestart} attempt(s) later. " +
            $"Last failure before the restart: {restart.Reason ?? "none recorded"}");
    }

    /// <summary>
    /// Why a retried test failed the first time.
    ///
    /// <para>The ledger named the flaky test but threw away the reason, and Bobcat's own log keeps
    /// only the <c>[FLAKY] ... passed on attempt 2</c> line for a test that eventually passed — the
    /// first attempt's failure appears nowhere at all. That is the difference between a lead and a
    /// name. Two of the four flaky tests standing on 2026-08-03 were undiagnosable for exactly this
    /// reason, and the note left on <c>multi_tenancy_through_virtual_hosts</c> records its next step
    /// as "dump the tracked session on the FIRST attempt rather than infer from the assertion" —
    /// which is this. GH-3763, GH-3787.</para>
    ///
    /// <para>Takes the earliest attempt that did not succeed rather than <c>Attempts[0]</c>: the
    /// ordering of the collection is Bobcat's business, and <c>AttemptNumber</c> is the field that
    /// actually states it.</para>
    ///
    /// <para>The reason comes from the attempt's <c>Outcome</c> — the test's own error — and
    /// deliberately <b>not</b> from <c>Disposition.Reason</c>, which is the supervisor's rationale
    /// for retrying ("a failure is retried in a fresh process, within the budget, to separate flaky
    /// from broken") and is the same sentence for every retry in the repository. That distinction is
    /// the whole point of this field: one of them identifies the test's problem, the other only
    /// restates the retry policy.</para>
    /// </summary>
    static FlakyFailure firstFailure(TestReport report)
    {
        var failed = report.Attempts?
            .Where(x => !x.Succeeded)
            .OrderBy(x => x.AttemptNumber)
            .FirstOrDefault();

        var outcome = failed?.Outcome;

        return new FlakyFailure
        {
            Test = report.DisplayName,
            Attempt = failed?.AttemptNumber ?? 0,
            ErrorType = string.IsNullOrWhiteSpace(outcome?.ErrorType) ? null : outcome.ErrorType,
            // One line and bounded: this is a lead to open the log with, not a copy of it. A
            // multi-line reason would also break the ::warning workflow command downstream.
            Reason = condense(outcome?.ErrorMessage),
            Stack = topFrames(outcome?.StackTrace)
        };
    }

    /// <summary>
    /// The top frames of the failing attempt's stack, for the ledger JSON only.
    ///
    /// <para>The message says what went wrong; the stack is what says <i>where</i>, and for a whole
    /// class of flake that is the only thing that distinguishes the candidates. The MQTT
    /// <c>Broken pipe</c> flake is the worked example: the message alone is equally consistent with a
    /// teardown ordering bug, a port collision between worker processes, and a keep-alive drop under
    /// CI load. Three local experiments eliminated the first two and the third needs a call site, so
    /// the investigation stalls on precisely this field.</para>
    ///
    /// <para>Deliberately kept out of the step summary, the annotation and the roll-up — all three are
    /// glanceable surfaces and a stack would drown them. The JSON ledger is already a downloadable
    /// artifact, which is the right place for something you go looking for on purpose.</para>
    /// </summary>
    static string[] topFrames(string stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return [];

        // Enough to cross a teardown/dispatch boundary and name the caller, not so much that the
        // ledger becomes a copy of the log.
        const int maxFrames = 12;

        return stackTrace
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Take(maxFrames)
            .ToArray();
    }

    /// <summary>
    /// Collapses a failure reason to a single bounded line, or null when there is nothing to say.
    /// </summary>
    static string condense(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;

        var flattened = string.Join(" ", reason.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0));

        const int limit = 400;
        return flattened.Length <= limit ? flattened : flattened[..limit] + "…";
    }

    /// <summary>
    /// Strips anything that cannot appear in a file name. The ledger's name is composed from values
    /// that are chosen elsewhere, and a stray path separator in one of them does not fail loudly —
    /// <see cref="writeLedgerFile"/> catches, warns and carries on by design, so the ledger simply
    /// goes missing and the roll-up reports the job as unmeasured. Cheaper to make that unreachable.
    /// </summary>
    static string fileNameSafe(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        // Not invalid, but a file name with spaces in it is a nuisance in every shell that later
        // touches the artifact.
        return name.Replace(' ', '-');
    }

    static void writeLedgerFile(LedgerEntry entry)
    {
        try
        {
            LedgerDirectory.CreateDirectory();

            // One file per project+framework: a target can run several projects, and a project can
            // run under several TFMs, so neither alone is a unique key.
            var fileName = fileNameSafe($"{entry.Job}.{entry.Project}.{entry.Framework}.json");
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

            if (entry.IsPartial)
            {
                builder.AppendLine();
                builder.AppendLine(
                    "> **PARTIAL** — the job was cancelled mid-run. These counts are the run so far; " +
                    "tests without a verdict are counted Indeterminate, not failed.");
            }

            if (entry.StalledTests.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine(
                    "> **Stalled (in flight past the 5-minute threshold):** " +
                    string.Join(", ", entry.StalledTests.Select(t => $"`{t}`")));
            }

            if (entry.FlakyTests.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("<details><summary>Tests that only passed on a retry</summary>");
                builder.AppendLine();

                // Keyed off FlakyFailures where it has something to add, so the reason sits with the
                // name rather than in a log nobody opens.
                var reasons = entry.FlakyFailures.ToDictionary(x => x.Test, x => x);
                foreach (var test in entry.FlakyTests)
                {
                    builder.AppendLine($"- `{test}`");

                    if (!reasons.TryGetValue(test, out var failure)) continue;
                    if (failure.ErrorType is null && failure.Reason is null) continue;

                    var label = failure.ErrorType is null ? "" : $"**{failure.ErrorType}**";
                    var detail = failure.Reason is null ? "" : $" — {failure.Reason}";
                    builder.AppendLine($"  - attempt {failure.Attempt}: {label}{detail}");
                }

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

        // Workflow commands are newline-delimited, so every message has to be a single line.

        if (entry.RetriesPerformed > 0)
        {
            var suspects = entry.FlakyTests.Length > 0
                ? $" First flaky test: {entry.FlakyTests[0]}."
                : "";

            Console.WriteLine(
                $"::warning title={entry.Job}: {entry.RetriesPerformed} retries::" +
                $"{entry.Project} ({entry.Framework}) spent {entry.RetriesPerformed} of its " +
                $"{MaxRetriesPerRun}-retry budget; " +
                $"{entry.PassedOnRetry} test(s) passed only on a retry.{suspects}");
        }

        if (entry.Stalled > 0)
        {
            Console.WriteLine(
                $"::warning title={entry.Job}: {entry.Stalled} stalled test(s)::" +
                $"{entry.Project} ({entry.Framework}) had test(s) in flight past the 5-minute " +
                $"stall threshold. First: {entry.StalledTests[0]}.");
        }

        if (entry.IsPartial)
        {
            Console.WriteLine(
                $"::warning title={entry.Job}: partial results::" +
                $"{entry.Project} ({entry.Framework}) was cancelled mid-run; the uploaded ledger " +
                "holds the run so far, with unverdicted tests counted Indeterminate.");
        }
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

        /// <summary>
        /// The same tests as <see cref="FlakyTests"/>, each with the reason its first attempt failed.
        /// Additive on purpose: <see cref="FlakyTests"/> stays a plain string array because
        /// build/flakiness-report.sh flattens it with <c>map(.FlakyTests[])</c>, and that jq must keep
        /// working against ledgers written by older runs.
        /// </summary>
        public FlakyFailure[] FlakyFailures { get; init; } = [];

        /// <summary>
        /// Containers a readiness gate had to restart before the service came up. Additive and
        /// defaulted for the same reason as <see cref="FlakyFailures"/>: the roll-up's baseline is
        /// downloaded from an EARLIER run's artifact, so the jq that reads this must survive a
        /// ledger written before the field existed. See GH-3855.
        /// </summary>
        public ServiceRestart[] ServiceRestarts { get; init; } = [];

        // The Bobcat 0.8.0 observability fields. All additive and defaulted — the roll-up's jq
        // reads them with `// 0` / `// false` / `// []` for the same baseline reason as above.

        /// <summary>
        /// True when this ledger came from the cancellation handler's Supervisor.Snapshot() —
        /// the run so far, not a verdict. A partial ledger's counts are honest but incomplete,
        /// and its Indeterminate column includes every test the run never got to.
        /// </summary>
        public bool IsPartial { get; init; }

        /// <summary>Tests reported in flight past the stall threshold, whether or not they finished.</summary>
        public int Stalled { get; init; }

        public string[] StalledTests { get; init; } = [];

        /// <summary>
        /// The highest worker RSS the supervisor sampled, in MB. Null when unmeasured — never
        /// zero, so "no memory data" can never read as "used no memory".
        /// </summary>
        public long? PeakWorkerRssMb { get; init; }
    }

    /// <summary>
    /// One container a readiness gate restarted. See <see cref="recordServiceRestart"/>.
    /// </summary>
    class ServiceRestart
    {
        /// <summary>Display name of the service, e.g. "SQL Server".</summary>
        public string Service { get; init; }

        /// <summary>Its docker compose service name, e.g. "sqlserver".</summary>
        public string ComposeService { get; init; }

        public int AttemptsBeforeRestart { get; init; }
        public int AttemptsAfterRestart { get; init; }

        /// <summary>The gate's last failure reason before the restart, flattened to one line.</summary>
        public string Reason { get; init; }
    }

    /// <summary>
    /// One retried test and why it failed before it passed. See <see cref="firstFailure"/>.
    /// </summary>
    class FlakyFailure
    {
        public string Test { get; init; }
        public int Attempt { get; init; }

        /// <summary>Exception type name from the failing attempt, e.g. ShouldAssertException.</summary>
        public string ErrorType { get; init; }

        /// <summary>The failing attempt's own error message, flattened to one bounded line.</summary>
        public string Reason { get; init; }

        /// <summary>
        /// Top frames of the failing attempt's stack. Ledger JSON only — see <see cref="topFrames"/>
        /// for why it is not rendered on the summary surfaces.
        /// </summary>
        public string[] Stack { get; init; } = [];
    }
}
