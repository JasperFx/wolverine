using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

// `./build.sh LocalGate --affected`: the gate, narrowed to what a change can reach.
//
// dotnet-affected (https://github.com/leonardochaia/dotnet-affected, pinned in .config/dotnet-tools.json)
// diffs git from a base commit to the working tree, evaluates every project under the repository with
// MSBuild, attributes each changed file to the projects that pull it in -- linked files such as src/Servers.cs
// and imports such as Directory.Build.props included, and a Directory.Packages.props change only to the
// projects that reference a package whose version moved -- and walks ProjectReferences to every project
// that depends on one of them. A suite runs when its test project is in that set; an extra (the AOT,
// samples, command-line and codegen gates) when one of the projects it builds is.
//
// It discovers projects by scanning the directory rather than reading wolverine.slnx on purpose: the Bobcat
// samples and the F# fixtures are not in that solution, and both are gates.
//
// What the project graph cannot see, declared here instead:
//
//   - A change to the gate's own machinery (build/, docker-compose.yml, global.json, xunit.runner.json, ...)
//     can change how every suite runs, so it runs everything. GateInfrastructure lists the paths.
//   - The F# compile gates build their fixture by shelling `dotnet build`, not by ProjectReference, so those
//     suites declare the fixture's directory in GateSuite.Watches.
//   - An opt-in suite joins an --affected run when a change touches its own area (GateSuite.JoinsWhenChanged):
//     the Azure Service Bus suite runs when a change touches src/Transports/Azure, not whenever core moves.
//
// The base is the merge base with origin/main unless --affected-from names one. Uncommitted and untracked
// changes count, so the gate can run before the commit it gates.
partial class Build
{
    [Parameter("LocalGate: run only the suites and extras a change since --affected-from (default: the merge base with origin/main) can reach")]
    readonly bool Affected;

    [Parameter("LocalGate --affected: the commit to compare the working tree against (default: git merge-base HEAD origin/main)")]
    readonly string AffectedFrom;

    /// <summary>
    /// Paths outside any project whose change runs the whole gate under --affected: the gate's own build, the
    /// containers, the SDK and the test platform's configuration. A trailing slash is a directory.
    /// </summary>
    static readonly string[] GateInfrastructure =
    [
        "build/", "docker/", ".config/",
        "build.sh", "build.ps1", "build.cmd",
        "global.json", "NuGet.config", "xunit.runner.json", ".editorconfig",
        "docker-compose.yml",
        "Directory.Build.props", "Directory.Build.targets", "Analysis.Build.props"
    ];

    /// <summary>
    /// Central package management: dotnet-affected diffs the package versions at both revisions and marks only
    /// the projects that reference a package that moved, which is the whole point for a dependency bump. It does
    /// that only for PackageVersion items, so a change to anything else in the file (a property such as
    /// CentralPackageTransitivePinningEnabled, an import) runs everything.
    /// </summary>
    const string CentralPackages = "Directory.Packages.props";

    /// <summary>
    /// What changed since the base, and every project that change reaches. Computed once per build.
    /// </summary>
    sealed record AffectedChanges(string Base, string[] ChangedFiles, HashSet<string> Projects, string[] Infrastructure)
    {
        public bool EverythingRuns => Infrastructure.Length > 0;

        /// <summary>A project path (repository-relative, forward slashes) or a directory prefix of one is affected.</summary>
        public bool Reaches(string pathOrPrefix) =>
            EverythingRuns || Projects.Any(p => p.StartsWith(pathOrPrefix, StringComparison.OrdinalIgnoreCase));

        public bool Touches(string prefix) =>
            ChangedFiles.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    AffectedChanges _affectedChanges;

    AffectedChanges affectedChanges() => _affectedChanges ??= computeAffectedChanges();

    AffectedChanges computeAffectedChanges()
    {
        var @base = string.IsNullOrWhiteSpace(AffectedFrom) ? mergeBaseWithMain() : AffectedFrom.Trim();

        var changed = gitLines($"diff --name-only {@base}")
            .Concat(gitLines("ls-files --others --exclude-standard"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var infrastructure = changed.Where(isGateInfrastructure).ToArray();
        if (changed.Contains(CentralPackages) && !onlyPackageVersionsChanged(@base))
            infrastructure = [.. infrastructure, $"{CentralPackages} (more than PackageVersion items)"];

        var output = (AbsolutePath)Path.Combine(Path.GetTempPath(), $"wolverine-affected-{Guid.NewGuid():N}");
        output.CreateDirectory();
        try
        {
            ProcessTasks.StartProcess("dotnet", "tool restore", workingDirectory: RootDirectory, logOutput: false)
                .AssertZeroExitCode();

            // No --filter-file-path: scan the directory (see the top of this file). Exit 166 is "nothing changed
            // and nothing is affected", and writes no file.
            var run = ProcessTasks.StartProcess("dotnet",
                $"tool run dotnet-affected --from {@base} --format json --output-dir \"{output}\" --output-name affected",
                workingDirectory: RootDirectory, logOutput: false);
            run.WaitForExit();

            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (run.ExitCode == 0)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(output / "affected.json"));
                foreach (var project in json.RootElement.EnumerateArray())
                {
                    projects.Add(Path.GetRelativePath(RootDirectory, project.GetProperty("FilePath").GetString()!)
                        .Replace('\\', '/'));
                }
            }
            else if (run.ExitCode != 166)
            {
                throw new InvalidOperationException(
                    $"dotnet-affected exited {run.ExitCode}:{Environment.NewLine}" +
                    string.Join(Environment.NewLine, run.Output.Select(x => x.Text)));
            }

            var changes = new AffectedChanges(@base, changed, projects, infrastructure);

            Log.Information("--affected: {Files} file(s) changed since {Base}, reaching {Projects} project(s)",
                changed.Length, @base, projects.Count);
            if (changes.EverythingRuns)
                Log.Information("--affected: everything runs, because the gate's own inputs changed: {Files}",
                    string.Join(", ", infrastructure));

            return changes;
        }
        finally
        {
            output.DeleteDirectory();
        }
    }

    /// <summary>
    /// Every added or removed line of the root Directory.Packages.props is a PackageVersion item, a comment or
    /// blank. A file that is new or deleted outright fails this, so it runs everything.
    /// </summary>
    bool onlyPackageVersionsChanged(string @base)
    {
        var lines = gitLines($"diff --unified=0 {@base} -- {CentralPackages}")
            .Where(x => (x.StartsWith('+') || x.StartsWith('-')) && !x.StartsWith("+++") && !x.StartsWith("---"))
            .Select(x => x[1..].Trim())
            .ToArray();

        return lines.Length > 0 && lines.All(x =>
            x.Length == 0 || x.StartsWith("<PackageVersion ") || x.StartsWith("<!--") || x.EndsWith("-->"));
    }

    static bool isGateInfrastructure(string file) =>
        GateInfrastructure.Any(x => x.EndsWith('/')
            ? file.StartsWith(x, StringComparison.OrdinalIgnoreCase)
            : string.Equals(file, x, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Narrows LocalGateSuites' selection to the suites the change reaches, after adding the opt-in suites
    /// whose own area it touches. --gate-only still wins: with it, nothing joins.
    /// </summary>
    GateSuite[] selectAffectedSuites(GateSuite[] selected)
    {
        var changes = affectedChanges();

        var joining = GateOnly is { Length: > 0 }
            ? []
            : GateSuites
                .Where(s => s.OptIn && !selected.Contains(s))
                .Where(s => s.JoinsWhenChanged.Any(changes.Touches))
                .ToArray();
        foreach (var suite in joining)
            Log.Information("--affected: {Suite} joins: the change touches {Paths}", suite.Name,
                string.Join(", ", suite.JoinsWhenChanged.Where(changes.Touches)));

        var candidates = selected.Concat(joining).ToArray();
        var affected = candidates.Where(s => changes.Reaches(s.Path) || s.Watches.Any(changes.Reaches)).ToArray();

        // Whichever list is the shorter one
        var skipped = candidates.Except(affected).Select(s => s.Name).ToArray();
        Log.Information("--affected: {Affected} of {Candidates} suite(s) affected{Which}",
            affected.Length, candidates.Length,
            skipped.Length == 0 || affected.Length == 0 ? ""
            : affected.Length <= skipped.Length ? ": " + string.Join(", ", affected.Select(s => s.Name))
            : "; not running " + string.Join(", ", skipped));

        return affected;
    }

    /// <summary>
    /// Whether an extra (see GateExtras) runs: always without --affected, otherwise when the change reaches one
    /// of the projects it builds.
    /// </summary>
    bool gateExtraSelected(string target)
    {
        if (!Affected) return true;

        var extra = GateExtras.Single(x => x.Target == target);
        var changes = affectedChanges();
        var selected = extra.Inputs.Any(changes.Reaches);
        if (!selected)
            Log.Information("--affected: skipping {Target}: the change reaches none of the project(s) it builds ({Inputs})",
                target, string.Join(", ", extra.Inputs));
        return selected;
    }

    string mergeBaseWithMain()
    {
        var run = ProcessTasks.StartProcess("git", "merge-base HEAD origin/main", workingDirectory: RootDirectory, logOutput: false);
        run.WaitForExit();
        if (run.ExitCode != 0 || run.Output.Count == 0)
            throw new InvalidOperationException(
                "--affected could not find the merge base with origin/main. Fetch it, or name the base with --affected-from <commit>.");
        return run.Output.First().Text.Trim();
    }

    IEnumerable<string> gitLines(string arguments)
    {
        var run = ProcessTasks.StartProcess("git", arguments, workingDirectory: RootDirectory, logOutput: false);
        run.AssertZeroExitCode();
        return run.Output.Select(x => x.Text.Trim()).Where(x => x.Length > 0);
    }
}
