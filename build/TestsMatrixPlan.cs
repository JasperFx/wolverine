using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nuke.Common;
using Serilog;

// The plan job of tests.yml: which of the workflow's matrix targets a pull request runs.
//
// The full matrix is listed in tests.yml itself, in the plan step's TESTS_MATRIX block, so the workflow stays
// the one place that says what CI runs (and VerifyCITargetCoverage still finds every target there). This
// target narrows that list with the same rules as `LocalGate --affected` (build/AffectedGate.cs): a target
// runs when one of the suites whose CITargets name it is reached by the change, or, for an extra such as
// CIAotSmoke, when one of the projects it builds is.
//
// It errs toward running things. Without --affected (pushes to main, a manual run), when the gate's own
// inputs or tests.yml changed, when a target maps to no suite or extra, and when dotnet-affected itself fails,
// the target runs. A skipped job that should have run is a regression nobody sees; an extra job is minutes.
//
// Writes `targets` (a JSON array for fromJson) and `count` to GITHUB_OUTPUT, and a table to the step summary.
// Run locally with TESTS_MATRIX set to see what a branch would get.
partial class Build
{
    const string TestsMatrixVariable = "TESTS_MATRIX";

    /// <summary>A change to these runs the whole matrix: the definition of the matrix itself.</summary>
    static readonly string[] TestsMatrixInputs = [".github/workflows/tests.yml"];

    Target PlanTestsMatrix => _ => _
        .Description("tests.yml's plan job: the matrix targets a change reaches (with --affected), as GITHUB_OUTPUT")
        .Executes(() =>
        {
            var all = (Environment.GetEnvironmentVariable(TestsMatrixVariable) ?? "")
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith('#'))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (all.Length == 0)
                throw new InvalidOperationException(
                    $"{TestsMatrixVariable} is empty. tests.yml's plan step lists the full matrix there, one target per line.");

            var (selected, reason) = selectTestsMatrix(all);
            var skipped = all.Except(selected).ToArray();

            Log.Information("{Selected} of {All} matrix target(s) run: {Reason}", selected.Length, all.Length, reason);
            if (skipped.Length > 0) Log.Information("Not running: {Skipped}", string.Join(", ", skipped));

            var json = JsonSerializer.Serialize(selected);
            Console.WriteLine(json);

            appendGitHubFile("GITHUB_OUTPUT", $"targets={json}\ncount={selected.Length}\n");
            appendGitHubFile("GITHUB_STEP_SUMMARY", testsMatrixSummary(selected, skipped, reason));
        });

    (string[] Selected, string Reason) selectTestsMatrix(string[] all)
    {
        if (!Affected) return (all, "the full matrix (no --affected)");

        AffectedChanges changes;
        try
        {
            changes = affectedChanges();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not work out what the change affects; running the full matrix");
            return (all, $"the full matrix, because working out what the change affects failed: {e.Message}");
        }

        if (changes.EverythingRuns)
            return (all, $"the full matrix, because the gate's own inputs changed ({string.Join(", ", changes.Infrastructure)})");

        var matrixChanges = TestsMatrixInputs.Where(changes.Touches).ToArray();
        if (matrixChanges.Length > 0)
            return (all, $"the full matrix, because the matrix itself changed ({string.Join(", ", matrixChanges)})");

        var selected = all.Where(target => testsMatrixTargetAffected(target, changes)).ToArray();
        return (selected,
            $"{changes.ChangedFiles.Length} file(s) changed since {changes.Base[..Math.Min(9, changes.Base.Length)]}, " +
            $"reaching {changes.Projects.Count} project(s)");
    }

    bool testsMatrixTargetAffected(string target, AffectedChanges changes)
    {
        var suites = GateSuites
            .Where(s => s.CITargets.Split(',').Any(x => string.Equals(x.Trim(), target, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var extras = GateExtras.Where(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (suites.Length == 0 && extras.Length == 0)
        {
            Log.Warning("{Target} is in the matrix but no LocalGate suite or extra names it, so it always runs. " +
                        "Name it in a suite's CITargets in build/LocalGate.cs", target);
            return true;
        }

        return suites.Any(s => changes.Reaches(s.Path) || s.Watches.Any(changes.Reaches))
               || extras.Any(x => x.Inputs.Any(changes.Reaches));
    }

    static string testsMatrixSummary(string[] selected, string[] skipped, string reason)
    {
        var summary = new StringBuilder()
            .AppendLine("## Test matrix")
            .AppendLine()
            .AppendLine($"**{selected.Length} of {selected.Length + skipped.Length}** target(s) run: {reason}.")
            .AppendLine();

        if (selected.Length > 0) summary.AppendLine($"Running: {string.Join(", ", selected.Select(x => $"`{x}`"))}").AppendLine();
        if (skipped.Length > 0)
            summary.AppendLine($"Not running, because the change reaches none of their test projects: " +
                               string.Join(", ", skipped.Select(x => $"`{x}`"))).AppendLine();

        return summary.ToString();
    }

    static void appendGitHubFile(string variable, string text)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, text);
    }
}
