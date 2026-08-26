using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

// GH-4100. A guard that no tracked-session budget can outlive the CI job cap.
//
// PulsarNativeReliabilityTests.run_setup_with_simulated_exception_in_handler asked for
// TrackActivity(TimeSpan.FromSeconds(1000)). 1000s is 16m40s against a 20 minute cap, and the suite
// spends six-plus minutes building and standing up Pulsar before its first test runs -- so the
// session's own timeout could never fire. When DotPulsar wedged, the job hit the cap first and was
// CANCELLED, and a cancelled job's logs are discarded outright (#4098). A budget longer than the cap
// converts every hang under it from a readable test failure with a tracking dump into an unreadable
// job cancellation with nothing at all.
//
// Two deliberate design choices, both from what VerifyCITargetCoverage learned (GH-3816):
//
//   * LITERALS ONLY. TrackActivity(Fixture.DefaultTimeout) and .Timeout(timeout) cannot be evaluated
//     from source text, and guessing at them is how a guard starts crying wolf. They are skipped
//     silently -- this checks what it can actually read.
//   * FINDING NOTHING IS NOT THE SAME AS NOTHING BEING WRONG. If the scan matches no budgets at all
//     the patterns have rotted or the tree moved, and that is reported as a broken guard rather than
//     as a clean build.
partial class Build
{
    /// <summary>
    /// The ceiling for any literal tracked-session budget.
    ///
    /// Derived rather than picked: the job cap is 20 minutes (timeout-minutes in tests.yml and
    /// dotnet.yml), and a test's budget has to fit in what is LEFT of that after restore, build,
    /// container startup and every test that ran before it -- which for the broker suites is six
    /// minutes and up. Five minutes leaves fifteen for all of that.
    ///
    /// It is also exactly the largest budget in the tree today (.Timeout(5.Minutes())), so this
    /// admits every existing test and rejects only new outliers. Raising it means arguing that a
    /// suite can hang for longer than that and still leave the job able to report.
    /// </summary>
    static readonly TimeSpan MaximumTrackedSessionBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TrackActivity(...) and .Timeout(...) taking a TimeSpan, plus the millisecond overloads on
    /// SendMessageAndWaitAsync / PublishMessageAndWaitAsync. Each captures the number and its unit.
    /// </summary>
    static readonly Regex[] BudgetPatterns =
    [
        new(@"\b(?:TrackActivity|Timeout)\(\s*TimeSpan\.From(?<unit>Milliseconds|Seconds|Minutes|Hours)\(\s*(?<value>\d+(?:\.\d+)?)\s*\)",
            RegexOptions.Compiled),
        new(@"\b(?:TrackActivity|Timeout)\(\s*(?<value>\d+(?:\.\d+)?)\s*\.(?<unit>Milliseconds|Seconds|Minutes|Hours)\(\s*\)",
            RegexOptions.Compiled),
        new(@"\btimeoutInMilliseconds\s*:\s*(?<value>\d+)(?<unit>)", RegexOptions.Compiled)
    ];

    Target VerifyTrackedSessionTimeouts => _ => _
        .Description(
            "Fails when a literal tracked-session budget exceeds what the CI job cap can report on. GH-4100.")
        .Executes(() =>
        {
            var offenders = new List<string>();
            var examined = 0;

            foreach (var file in sourceFilesToScan())
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var pattern in BudgetPatterns)
                    {
                        foreach (Match match in pattern.Matches(lines[i]))
                        {
                            var budget = budgetFrom(match);
                            if (budget is null) continue;

                            examined++;
                            if (budget <= MaximumTrackedSessionBudget) continue;

                            var relative = Path.GetRelativePath(RootDirectory, file);
                            offenders.Add($"  {relative}:{i + 1}  {budget.Value.TotalSeconds:0}s  {match.Value.Trim()}");
                        }
                    }
                }
            }

            if (examined == 0)
            {
                throw new InvalidOperationException(
                    $"Found no tracked-session budgets at all under {RootDirectory / "src"}. The patterns have " +
                    "rotted or the tree moved -- that is a broken guard, not a clean build.");
            }

            if (offenders.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{offenders.Count} tracked-session budget(s) exceed {MaximumTrackedSessionBudget.TotalMinutes:0} minutes, " +
                    "so a hang under them cancels the CI job instead of failing the test -- and a cancelled job's logs " +
                    $"are discarded (GH-4098). See GH-4100.{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
            }

            Log.Information("Tracked-session timeouts: {Examined} literal budgets, all within {Max} minutes",
                examined, MaximumTrackedSessionBudget.TotalMinutes);
        });

    static IEnumerable<string> sourceFilesToScan()
    {
        return Directory
            .EnumerateFiles(RootDirectory / "src", "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    static TimeSpan? budgetFrom(Match match)
    {
        if (!double.TryParse(match.Groups["value"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        // The timeoutInMilliseconds pattern has no unit to capture
        return match.Groups["unit"].Value switch
        {
            "Hours" => TimeSpan.FromHours(value),
            "Minutes" => TimeSpan.FromMinutes(value),
            "Seconds" => TimeSpan.FromSeconds(value),
            "Milliseconds" or "" => TimeSpan.FromMilliseconds(value),
            _ => null
        };
    }
}
