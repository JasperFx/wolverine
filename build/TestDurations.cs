using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Bobcat.Supervisor;
using Nuke.Common.IO;
using Serilog;

// Per-test durations fed back into Supervisor.KnownTestDurations — the follow-up the ledger work
// left queued (BOBCAT-CI-OPTIMIZATION-HANDOFF, "Timing data worth keeping between runs").
//
// Why: Bobcat balances lanes longest-processing-time-first, but a run with no duration data falls
// back to test COUNT — measured on PersistenceTests as lanes finishing at 101.5s and 11.4s, a
// quarter of the fleet idle. The durations exist on every run's SupervisorResults and were being
// thrown away.
//
// The loop: every supervised run writes {Job}.{Project}.{Framework}.durations.json beside its
// ledger (so the existing per-job artifact upload carries it); the flakiness roll-up folds every
// job's durations into the run-level `test-ledger-run` artifact it already publishes from main;
// the next run's test jobs download that artifact into previous-durations/ (repo root — anything
// under artifacts/ is wiped by Clean at the start of every build) before ./build.sh runs, and
// runSupervised feeds the matching file into KnownTestDurations. Every hop is best-effort: no
// baseline, a stale baseline, or a renamed test all degrade to exactly today's behavior — count
// balancing, with unknown tests charged the median of what is known.
//
// Honest scope note: lane balancing only bites where MaxParallelWorkers > 1 — today CISqlServer
// (workers: 4) and local --test-workers runs. Everything else gets the data recorded for free,
// which is also the groundwork for duration trend reporting (Bobcat #56 layer 3).
partial class Build
{
    /// <summary>
    /// Where a CI step (or a curious developer) drops the previous main run's published
    /// durations. Repo root, NOT under artifacts/: Clean wipes artifacts/ on every invocation,
    /// and this file must survive into the test target.
    /// </summary>
    static AbsolutePath PreviousDurationsDirectory => RootDirectory / "previous-durations";

    static string durationsFileName(string projectName, string framework)
        => fileNameSafe($"{ledgerJobName}.{projectName}.{framework}.durations.json");

    /// <summary>
    /// Writes this run's per-test durations beside the ledger. First-attempt durations, because
    /// lane balancing wants the typical cost of running the test once — a retry-amplified total
    /// would overweight exactly the flaky tests. A test with no reported duration is omitted,
    /// never zero-filled: Bobcat charges absent tests the median of what is known, and a zero
    /// would instead read as "free".
    /// </summary>
    static void recordDurations(string projectName, string framework, SupervisorResults results)
    {
        try
        {
            var durations = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var test in results.Tests)
            {
                var measured = test.Attempts
                    .OrderBy(a => a.AttemptNumber)
                    .Select(a => a.Outcome.Duration)
                    .FirstOrDefault(d => d is not null);

                if (measured is { } duration) durations[test.Uid] = (long)duration.TotalMilliseconds;
            }

            if (durations.Count == 0) return;

            LedgerDirectory.CreateDirectory();
            File.WriteAllText(
                LedgerDirectory / durationsFileName(projectName, framework),
                JsonSerializer.Serialize(durations, LedgerJson));
        }
        catch (Exception e)
        {
            // Same rule as the ledger: reporting about the tests must never fail the tests.
            Log.Warning(e, "Could not write test durations for {Project}", projectName);
        }
    }

    /// <summary>
    /// The previous main run's durations for this job+project+framework, or null when there are
    /// none — which is not an error, it is the first run, a new shard, or a build outside CI.
    /// Searched recursively because the artifact download may or may not preserve its
    /// durations/ subdirectory depending on how it was fetched.
    /// </summary>
    static IReadOnlyDictionary<string, TimeSpan> knownDurationsFor(string projectName, string framework)
    {
        try
        {
            if (!Directory.Exists(PreviousDurationsDirectory)) return null;

            var wanted = durationsFileName(projectName, framework);
            var file = Directory
                .EnumerateFiles(PreviousDurationsDirectory, wanted, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (file is null) return null;

            var raw = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(file));
            if (raw is null || raw.Count == 0) return null;

            Log.Information("  balancing lanes with {Count} test duration(s) from the previous main run",
                raw.Count);

            return raw.ToDictionary(
                pair => pair.Key,
                pair => TimeSpan.FromMilliseconds(pair.Value),
                StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read previous test durations for {Project} — balancing by count", projectName);
            return null;
        }
    }
}
