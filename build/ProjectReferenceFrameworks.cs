using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nuke.Common;
using Serilog;

// GH-5622. A guard that no project in wolverine.slnx references a project declaring a single
// <TargetFramework>.
//
// Such a project ignores the repository's <TargetFrameworks> and is single-targeted, and a reference
// to a single-targeted project is built with TargetFramework UNDEFINED. Compile builds the solution
// with `-f net10.0`, so the solution's own build of that project has TargetFramework=net10.0: two sets
// of global properties, two instances of one project, both writing the same obj/ and bin/ files at
// once. That is the intermittent MSB3030 on obj/Release/net10.0/apphost (RetailClient -> Orders).
//
// The fix is <TargetFrameworks>net10.0</TargetFrameworks> in the referenced project, NOT
// SetTargetFramework on the reference:
//
//   * A one-entry <TargetFrameworks> makes the project multi-targeted, so every reference negotiates
//     TargetFramework=net10.0, the same instance the pinned solution build makes, and the unpinned
//     build's outer build hands off to that same inner build.
//   * SetTargetFramework only pins the reference it is written on. The SDK adds TRANSITIVE project
//     references without it (RetailClient -> Orders -> Messages still built Messages twice), and an
//     unpinned `dotnet build wolverine.slnx` then builds the pinned reference beside the solution's
//     TargetFramework-less build of it: the same race, moved.
//
// FINDING NOTHING IS NOT THE SAME AS NOTHING BEING WRONG (see VerifyTrackedSessionTimeouts): if the
// scan reads no project references at all, the solution moved or the pattern rotted, and that is a
// broken guard rather than a clean one.
partial class Build
{
    Target VerifyProjectReferenceFrameworks => _ => _
        .Description(
            "Fails when a project in wolverine.slnx references a project that declares a single <TargetFramework>. GH-5622.")
        .Executes(verifyProjectReferenceFrameworks);

    void verifyProjectReferenceFrameworks()
    {
        var solution = File.ReadAllText(RootDirectory / "wolverine.slnx");
        var projects = Regex.Matches(solution, @"Path=""([^""]+\.(?:cs|fs)proj)""")
            .Select(m => Path.GetFullPath(Path.Combine(RootDirectory, m.Groups[1].Value.Replace('\\', '/'))))
            .Where(File.Exists)
            .Distinct()
            .ToArray();

        var references = 0;
        var violations = new List<string>();

        foreach (var project in projects)
        {
            foreach (var referenced in projectReferencesOf(project))
            {
                references++;
                if (declaresSingleTargetFramework(referenced))
                {
                    violations.Add($"{relative(project)} -> {relative(referenced)}");
                }
            }
        }

        if (references == 0)
        {
            throw new InvalidOperationException(
                "Read no ProjectReference in wolverine.slnx. That is a broken guard, not a clean one.");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                "These references point at a project that declares a single <TargetFramework>, which the pinned " +
                "solution build (-f net10.0) builds twice at once on the same obj/ files (GH-5622):" +
                Environment.NewLine + string.Join(Environment.NewLine, violations.Select(v => "  " + v)) +
                Environment.NewLine +
                "Change <TargetFramework> to <TargetFrameworks> in each referenced project (one entry is fine). " +
                "SetTargetFramework on the reference is not enough: transitive references do not carry it.");
        }

        Log.Information("Checked {References} project reference(s) from {Projects} project(s) in wolverine.slnx",
            references, projects.Length);
    }

    /// <summary>
    /// The project files a project references, resolved against its directory. An Include built from
    /// any property other than the project's own directory cannot be read from the file and is skipped.
    /// </summary>
    static IEnumerable<string> projectReferencesOf(string project)
    {
        var directory = Path.GetDirectoryName(project)!;

        foreach (var element in XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = (string)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include)) continue;

            foreach (var entry in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var path = entry
                    .Replace("$(MSBuildThisFileDirectory)", directory + "/")
                    .Replace("$(MSBuildProjectDirectory)", directory)
                    .Replace('\\', '/');
                if (path.Contains("$(")) continue;

                var full = Path.GetFullPath(Path.Combine(directory, path));
                if (File.Exists(full)) yield return full;
            }
        }
    }

    static bool declaresSingleTargetFramework(string project) =>
        XDocument.Load(project).Descendants()
            .Any(e => e.Name.LocalName == "TargetFramework" && !string.IsNullOrWhiteSpace(e.Value));

    string relative(string path) => Path.GetRelativePath(RootDirectory, path).Replace('\\', '/');
}
