using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.Execution;
using Serilog;

// GH-3816. A guard that every CI* target actually runs somewhere.
//
// The motivation is earned: SlowTests existed, was maintained, and was cited in issue write-ups
// while running in ZERO CI jobs, and nothing anywhere reported that.
//
// The reason this took a second attempt is the shape of the obvious version. Diffing declared
// targets against target names grepped out of the workflow files reports CIMessageRouting on its
// first run -- which is named in no workflow and yet runs on every push, via `Target CI =>
// .DependsOn(CoreTests, CIMessageRouting)` and dotnet.yml's `./build.sh ci`. A guard that cries wolf
// immediately is how a signal channel gets ignored, which is the exact failure this reporting effort
// exists to undo; build/flakiness-report.sh carries NO_LEDGER_EXPECTED for the same reason.
//
// So this walks the real graph. Nuke hands us ExecutableTarget with ExecutionDependencies (DependsOn),
// Triggers and TriggerDependencies (TriggeredBy), which is the actual answer to "if CI runs, what
// runs with it" rather than a re-derivation of it from source text.
partial class Build
{
    /// <summary>
    /// Targets that are deliberately not reachable from any workflow. Empty today, and it should stay
    /// that way: an entry here is a claim that a target is meant to be manual, and it needs a reason
    /// beside it. CISlowTests is NOT here -- it is workflow_dispatch-only, but slow-tests.yml names
    /// it, so the walk finds it.
    /// </summary>
    static readonly Dictionary<string, string> DeliberatelyManualTargets = new(StringComparer.OrdinalIgnoreCase);

    Target VerifyCITargetCoverage => _ => _
        .Description("Fails when a CI* target is not reachable from any GitHub workflow. GH-3816.")
        .Executes(() =>
        {
            var targets = executableTargets().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            var invoked = invokedTargetNames(targets.Keys);

            if (invoked.Count == 0)
            {
                // Reading nothing is not the same as finding nothing wrong. If the workflow directory
                // moved, every target would look orphaned and this would report a wall of noise; say
                // what actually happened instead.
                throw new InvalidOperationException(
                    $"No workflow named any known build target. Looked in {RootDirectory / ".github" / "workflows"}. " +
                    "That is a broken guard rather than a broken build.");
            }

            var reachable = reachableFrom(invoked, targets);

            var orphans = targets.Values
                .Where(x => x.Name.StartsWith("CI", StringComparison.OrdinalIgnoreCase))
                .Where(x => !reachable.Contains(x.Name))
                .Where(x => !DeliberatelyManualTargets.ContainsKey(x.Name))
                // A target with no actions of its own cannot run anything its dependencies do not
                // already run, so an aggregate whose whole dependency set is covered is covered. This
                // is what keeps CIAWS -- `.DependsOn(CIAWSSqs, CIAWSSqsCompliance, CIAWSSns)`, invoked
                // by no workflow -- from being reported as a hole that does not exist.
                .Where(x => !isCoveredAggregate(x, reachable))
                .OrderBy(x => x.Name)
                .ToList();

            Log.Information(
                "GH-3816: {Invoked} target(s) invoked by workflows reach {Reachable} target(s); {Total} CI* target(s) declared",
                invoked.Count, reachable.Count,
                targets.Values.Count(x => x.Name.StartsWith("CI", StringComparison.OrdinalIgnoreCase)));

            if (orphans.Count == 0)
            {
                Log.Information("Every CI* target is reachable from a workflow");
                return;
            }

            foreach (var orphan in orphans)
            {
                Log.Error("{Target} is declared but no workflow reaches it", orphan.Name);
            }

            throw new InvalidOperationException(
                $"{orphans.Count} CI target(s) run nowhere: {string.Join(", ", orphans.Select(x => x.Name))}. " +
                "Add the target to a workflow, or record it in DeliberatelyManualTargets with a reason.");
        });

    /// <summary>
    /// Nuke's own target graph. <c>NukeBuild.ExecutableTargets</c> is internal, so this is reflection --
    /// deliberately, because the alternative is re-deriving the graph by parsing DependsOn out of the
    /// source, which is exactly the brittleness GH-3816 was filed about. If a Nuke upgrade renames this,
    /// the guard fails loudly here rather than quietly reporting that everything is covered.
    /// </summary>
    IReadOnlyCollection<ExecutableTarget> executableTargets()
    {
        var property = typeof(NukeBuild).GetProperty("ExecutableTargets",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (property?.GetValue(this) is IReadOnlyCollection<ExecutableTarget> targets && targets.Count > 0)
        {
            return targets;
        }

        throw new InvalidOperationException(
            "Could not read NukeBuild.ExecutableTargets by reflection -- the Nuke API this guard walks has moved. " +
            "Fix the guard; do not delete it.");
    }

    /// <summary>
    /// Every declared target name mentioned by a workflow file, matched whole-word and case-insensitively.
    ///
    /// <para>Case matters here: dotnet.yml runs <c>./build.sh ci</c> in lower case, and Nuke resolves
    /// target names case-insensitively, so a case-sensitive match would miss the root that most of the
    /// graph hangs from.</para>
    ///
    /// <para>Matching any mention rather than parsing the invocation is deliberate. A matrix entry
    /// reaches the command line through <c>${{ matrix.target }}</c>, so there is no single syntax to
    /// parse, and the failure mode of over-matching (counting a target as covered when it is only
    /// named) is a missed orphan -- while under-matching produces the false alarm that made the first
    /// version of this guard unusable. Comment lines are skipped so that a target named only in a
    /// note is not mistaken for one that runs.</para>
    /// </summary>
    HashSet<string> invokedTargetNames(IEnumerable<string> declared)
    {
        var known = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = RootDirectory / ".github" / "workflows";

        if (!Directory.Exists(directory)) return found;

        foreach (var file in Directory.EnumerateFiles(directory, "*.yml").Concat(Directory.EnumerateFiles(directory, "*.yaml")))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("#")) continue;

                foreach (Match match in Regex.Matches(line, @"[A-Za-z][A-Za-z0-9_]*"))
                {
                    if (known.Contains(match.Value)) found.Add(match.Value);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Everything that runs when the given targets are invoked: the targets themselves, whatever they
    /// depend on, whatever they trigger, and whatever declares itself TriggeredBy one of them.
    /// </summary>
    static HashSet<string> reachableFrom(IEnumerable<string> roots, Dictionary<string, ExecutableTarget> targets)
    {
        // TriggeredBy is recorded on the triggered target, so the edge it implies runs the other way
        // and has to be inverted before the walk.
        var triggeredBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets.Values)
        {
            foreach (var trigger in target.TriggerDependencies)
            {
                if (!triggeredBy.TryGetValue(trigger.Name, out var list))
                {
                    triggeredBy[trigger.Name] = list = [];
                }

                list.Add(target.Name);
            }
        }

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!reachable.Add(name)) continue;
            if (!targets.TryGetValue(name, out var target)) continue;

            foreach (var next in target.ExecutionDependencies.Concat(target.Triggers).Select(x => x.Name))
            {
                queue.Enqueue(next);
            }

            if (triggeredBy.TryGetValue(name, out var triggered))
            {
                foreach (var next in triggered) queue.Enqueue(next);
            }
        }

        return reachable;
    }

    /// <summary>
    /// True for a target that does no work of its own and whose dependencies all run anyway.
    /// </summary>
    static bool isCoveredAggregate(ExecutableTarget target, HashSet<string> reachable)
    {
        return target.Actions.Count == 0
               && target.ExecutionDependencies.Count > 0
               && target.ExecutionDependencies.All(x => reachable.Contains(x.Name));
    }
}
