using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;

namespace Wolverine.Bobcat.EventModel;

/// <summary>What <see cref="SpecProjectWriter.Plan"/> is asked to set up.</summary>
/// <param name="SpecsDirectory">The spec project's directory, where the specifications are written.</param>
/// <param name="SpecsNamespace">The spec project's root namespace, which names its .csproj.</param>
/// <param name="ModelName">The model name, which <c>[assembly: EventModelName]</c> declares.</param>
/// <param name="AppProject">The application's .csproj, or null when none was found.</param>
/// <param name="BobcatVersion">The Bobcat packages' version; <see cref="SpecProjectWriter.BobcatVersion"/> by default.</param>
/// <param name="WolverineFxBobcatVersion">WolverineFx.Bobcat's version; <see cref="SpecProjectWriter.DefaultWolverineFxBobcatVersion"/> by default.</param>
public sealed record SpecProjectRequest(
    string SpecsDirectory,
    string SpecsNamespace,
    string ModelName,
    string? AppProject,
    string? BobcatVersion = null,
    string? WolverineFxBobcatVersion = null);

/// <summary>A spec project file to write, at a full path. The writer never returns a file that already exists.</summary>
public sealed record SpecProjectFile(string Path, string Content);

/// <summary>What <see cref="SpecProjectWriter.Plan"/> decided: the files to write, and what to tell the person.</summary>
public sealed record SpecProjectPlan(
    IReadOnlyList<SpecProjectFile> Files,
    IReadOnlyList<SpecProjectFile> Kept,
    IReadOnlyList<ImportCallout> Callouts,
    IReadOnlyList<string> Report);

/// <summary>
/// The spec project <c>write-specs</c> writes the specifications into: the .csproj, a
/// global.json and the assembly attributes, when the spec directory holds no project yet, and the
/// checks on the application the specifications run against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written once, never overwritten, not even with <c>--force</c>.</b> These are build files the
/// person owns from the first edit. When the spec directory already holds a .csproj nothing here is
/// written; the existing project is checked instead, and anything missing is a callout.
/// </para>
/// <para>
/// <b>The application is never edited.</b> A missing <c>public partial class Program</c>,
/// <c>RunJasperFxCommands</c> or WolverineFx.RuntimeCompilation reference is a callout carrying the
/// exact line or command that settles it.
/// </para>
/// <para>
/// <b>global.json goes where <c>dotnet test</c> will find it.</b> The SDK reads the global.json nearest
/// the directory the command runs in, so an existing one anywhere above the spec project governs it and
/// is checked, never shadowed. With none, it is written at the solution root (the nearest directory
/// above the spec project holding a .sln or .slnx, else the git root), so <c>dotnet test</c> from the
/// root and from the spec project both run on Microsoft.Testing.Platform; with neither, beside the
/// spec project.
/// </para>
/// </remarks>
public static class SpecProjectWriter
{
    /// <summary>
    /// The WolverineFx.Bobcat used when neither <c>--wolverine-version</c> nor the
    /// <c>WolverineFxBobcatVersion</c> property says otherwise: the version WolverineFx.Bobcat packed as in the
    /// Wolverine build that produced this package, stamped by that build (<c>WolverineFxBobcatVersion</c>
    /// assembly metadata).
    /// </summary>
    /// <remarks>
    /// Both packages ship from one Wolverine build (GH-5082), but not necessarily at one version string. This
    /// used to be <see cref="ToolVersion"/>, which was right only while both carried the same prerelease
    /// suffix: once WolverineFx.Bobcat leaves <c>-alpha</c> and this package does not, this package's own
    /// version names a WolverineFx.Bobcat that is never published, and the spec project fails to restore
    /// (NU1102). GH-5222.
    /// </remarks>
    public static string DefaultWolverineFxBobcatVersion
        => typeof(SpecProjectWriter).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(x => x.Key == "WolverineFxBobcatVersion")?.Value is { Length: > 0 } stamped
            ? stamped
            : throw new InvalidOperationException("This WolverineFx.Bobcat.EventModel carries no WolverineFxBobcatVersion metadata; pass --wolverine-version.");

    /// <summary>The file holding <c>[assembly: EventModelName]</c>, the parallelization and the content root.</summary>
    public const string AssemblyAttributesFile = "AssemblyAttributes.cs";

    public const string GlobalJsonFile = "global.json";

    // Third-party pins, the ones Bobcat's own projects and Wolverine's spec projects build with
    internal const string XunitV3Version = "4.0.2";
    internal const string TestSdkVersion = "17.14.1";
    internal const string XunitRunnerVisualStudioVersion = "4.0.1";
    internal const string AlbaVersion = "8.5.2";

    /// <summary>
    /// The Bobcat packages' version when <c>--bobcat-version</c> does not say: the Bobcat this package was built
    /// against, stamped by its build from the repository's central version (<c>BobcatVersion</c> assembly metadata).
    /// </summary>
    public static string BobcatVersion
        => typeof(SpecProjectWriter).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(x => x.Key == "BobcatVersion")?.Value is { Length: > 0 } stamped
            ? stamped
            : throw new InvalidOperationException("This WolverineFx.Bobcat.EventModel carries no BobcatVersion metadata; pass --bobcat-version.");

    /// <summary>
    /// This package's own version. Not necessarily WolverineFx.Bobcat's: that is
    /// <see cref="DefaultWolverineFxBobcatVersion"/> (GH-5222).
    /// </summary>
    public static string ToolVersion
    {
        get
        {
            var assembly = typeof(SpecProjectWriter).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (informational is { Length: > 0 }) return informational.Split('+')[0];
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>The packages the generated specifications need, with the property or version each takes.</summary>
    internal static IReadOnlyList<(string Id, string Version, bool Private)> Packages(string bobcat, string wolverine) =>
    [
        ("xunit.v3", XunitV3Version, false),
        ("Bobcat", bobcat, false),
        ("Bobcat.Xunit", bobcat, false),
        ("Bobcat.Generators", bobcat, true),
        ("WolverineFx.Bobcat", wolverine, false),
        ("Alba", AlbaVersion, false),
        ("Microsoft.NET.Test.Sdk", TestSdkVersion, false),
        ("xunit.runner.visualstudio", XunitRunnerVisualStudioVersion, true)
    ];

    /// <summary>
    /// The application project for <paramref name="outDirectory"/> (the --out directory): the one .csproj
    /// in it. Null when there is none, or several.
    /// </summary>
    public static string? FindAppProject(string outDirectory)
    {
        if (!Directory.Exists(outDirectory)) return null;
        var projects = Directory.GetFiles(outDirectory, "*.csproj");
        return projects.Length == 1 ? Path.GetFullPath(projects[0]) : null;
    }

    public static SpecProjectPlan Plan(SpecProjectRequest request)
    {
        var specs = Path.GetFullPath(request.SpecsDirectory);
        var bobcat = request.BobcatVersion is { Length: > 0 } b ? b : BobcatVersion;
        var wolverine = request.WolverineFxBobcatVersion is { Length: > 0 } w ? w : DefaultWolverineFxBobcatVersion;
        var files = new List<SpecProjectFile>();
        var kept = new List<SpecProjectFile>();
        var callouts = new List<ImportCallout>();
        var report = new List<string>();

        var app = request.AppProject is null ? null : Path.GetFullPath(request.AppProject);
        if (app is not null && !File.Exists(app)) app = null;

        var central = findUp(specs, "Directory.Packages.props") is { } packagesProps && isCentral(File.ReadAllText(packagesProps))
            ? packagesProps
            : null;

        var existing = Directory.Exists(specs) ? Directory.GetFiles(specs, "*.csproj") : [];
        string csprojPath;
        if (existing.Length > 0)
        {
            csprojPath = Path.GetFullPath(existing[0]);
            kept.Add(new SpecProjectFile(csprojPath, ""));
            callouts.Add(projectCallout(ImportCalloutCodes.SpecProjectFileKept, csprojPath,
                $"Kept the spec project {csprojPath}, which already exists; nothing in it was changed.", null));
            checkExistingProject(csprojPath, specs, request.ModelName, callouts);
        }
        else
        {
            csprojPath = Path.Combine(specs, request.SpecsNamespace + ".csproj");
            files.Add(new SpecProjectFile(csprojPath, WriteCsproj(request.SpecsNamespace, app, specs, bobcat, wolverine, central is not null)));
            report.Add($"spec project: {csprojPath}, on xunit.v3 {XunitV3Version} and Microsoft.Testing.Platform, Bobcat {bobcat}, "
                       + $"WolverineFx.Bobcat {wolverine} (--wolverine-version, or the WolverineFxBobcatVersion property, changes it).");

            var attributes = Path.Combine(specs, AssemblyAttributesFile);
            if (File.Exists(attributes))
            {
                kept.Add(new SpecProjectFile(attributes, ""));
                callouts.Add(projectCallout(ImportCalloutCodes.SpecProjectFileKept, attributes,
                    $"Kept {attributes}, which already exists. It needs [assembly: EventModelName(\"{request.ModelName}\")].",
                    $"[assembly: EventModelName(\"{request.ModelName}\")]"));
            }
            else
            {
                files.Add(new SpecProjectFile(attributes, WriteAssemblyAttributes(request.ModelName, app, specs)));
            }

            if (central is not null)
            {
                var declared = declaredPackageVersions(File.ReadAllText(central));
                var missing = Packages(bobcat, wolverine).Where(x => !declared.Contains(x.Id)).ToList();
                if (missing.Count > 0)
                {
                    var lines = string.Join("\n", missing.Select(x => $"<PackageVersion Include=\"{x.Id}\" Version=\"{x.Version}\" />"));
                    callouts.Add(projectCallout(ImportCalloutCodes.CentralPackageVersions, central,
                        $"{central} manages package versions centrally, so the spec project's PackageReferences carry no versions. Add: "
                        + string.Join(", ", missing.Select(x => $"{x.Id} {x.Version}")) + ".", lines));
                }
            }
        }

        planGlobalJson(specs, files, kept, callouts, report);
        checkSolution(specs, csprojPath, callouts);
        callouts.AddRange(CheckApp(app, specs));

        return new SpecProjectPlan(files, kept, callouts, report);
    }

    /// <summary>
    /// What the application needs for the generated specifications to run, each missing piece a callout
    /// with the exact fix. <paramref name="specsDirectory"/> is left out of the search when it sits inside the application.
    /// </summary>
    public static IReadOnlyList<ImportCallout> CheckApp(string? appProject, string? specsDirectory = null)
    {
        if (appProject is null || !File.Exists(appProject))
        {
            return
            [
                projectCallout(ImportCalloutCodes.AppProjectNotFound, appProject,
                    "No single application .csproj was found in --out, so the spec project has no ProjectReference to the application "
                    + "and the checks on Program were skipped. Pass --app-project, or add the reference by hand.",
                    "<ProjectReference Include=\"..\\<App>\\<App>.csproj\" />") with { Override = "--app-project <path/to/App.csproj>" }
            ];
        }

        var callouts = new List<ImportCallout>();
        var appDirectory = Path.GetDirectoryName(Path.GetFullPath(appProject))!;
        var sources = sourceFiles(appDirectory, specsDirectory).ToList();
        var texts = sources.Select(x => (Path: x, Text: File.ReadAllText(x))).ToList();
        var program = texts.FirstOrDefault(x => Path.GetFileName(x.Path) == "Program.cs").Path
                      ?? Path.Combine(appDirectory, "Program.cs");

        if (!texts.Any(x => publicProgram.IsMatch(x.Text)))
        {
            callouts.Add(projectCallout(ImportCalloutCodes.AppProgramNotPublic, program,
                "The application declares no public Program, and the specifications start it through Alba (AlbaHost.For<Program>), "
                + "which cannot see the one top-level statements generate. Add this line to the end of Program.cs.",
                "public partial class Program;"));
        }

        if (!texts.Any(x => x.Text.Contains("RunJasperFxCommands", StringComparison.Ordinal)))
        {
            callouts.Add(projectCallout(ImportCalloutCodes.AppMissingRunJasperFxCommands, program,
                "The application does not run through RunJasperFxCommands, so `wolverine scaffold`, `codegen` and the event model "
                + "commands cannot run against it. Replace the closing app.Run(); in Program.cs with this line.",
                "return await app.RunJasperFxCommands(args);"));
        }

        var csproj = File.ReadAllText(appProject);
        if (!csproj.Contains("WolverineFx.RuntimeCompilation", StringComparison.OrdinalIgnoreCase)
            && !csproj.Contains("Wolverine.RuntimeCompilation", StringComparison.OrdinalIgnoreCase))
        {
            var version = wolverineVersionOf(appProject);
            callouts.Add(projectCallout(ImportCalloutCodes.AppMissingRuntimeCompilation, appProject,
                "The application references no WolverineFx.RuntimeCompilation, so a host in TypeLoadMode.Dynamic (the default) fails "
                + "to start and every specification fails with it. Add the package, at the application's WolverineFx version.",
                $"dotnet add \"{appProject}\" package WolverineFx.RuntimeCompilation" + (version is null ? "" : $" --version {version}")));
        }

        return callouts;
    }

    /// <summary>The spec project's .csproj.</summary>
    public static string WriteCsproj(string specsNamespace, string? appProject, string specsDirectory, string bobcatVersion,
        string wolverineFxBobcatVersion, bool centralPackageManagement = false)
    {
        var framework = appProject is null ? "net10.0" : targetFrameworkOf(File.ReadAllText(appProject));
        var builder = new StringBuilder();
        builder.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n\n");
        builder.Append("  <!--\n");
        // No double hyphen anywhere in an XML comment (MSB4025), so the flag is not spelled out
        builder.Append("    Written by `write-specs` (WolverineFx.Bobcat.EventModel) because the spec directory held no project.\n");
        builder.Append("    Nothing regenerates it. An xUnit v3 test host on Microsoft.Testing.Platform 2: `dotnet test`\n");
        builder.Append("    runs it in the platform's mode (see global.json), and Microsoft.NET.Test.Sdk with\n");
        builder.Append("    xunit.runner.visualstudio is the bridge Rider and Visual Studio discover tests through.\n");
        builder.Append("  -->\n");
        builder.Append("  <PropertyGroup>\n");
        builder.Append("    <OutputType>Exe</OutputType>\n");
        builder.Append($"    <TargetFramework>{framework}</TargetFramework>\n");
        builder.Append("    <ImplicitUsings>enable</ImplicitUsings>\n");
        builder.Append("    <Nullable>enable</Nullable>\n");
        builder.Append("    <IsPackable>false</IsPackable>\n");
        builder.Append("    <IsTestProject>true</IsTestProject>\n");
        builder.Append($"    <RootNamespace>{specsNamespace}</RootNamespace>\n");
        builder.Append("    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>\n");
        builder.Append("    <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>\n");
        builder.Append("    <!-- BOBCAT037: the generator's step interceptors live in Bobcat.Generated. The Bobcat.Generators\n");
        builder.Append("         package adds this too; stated here so the project builds the same without its props. -->\n");
        builder.Append("    <InterceptorsNamespaces>$(InterceptorsNamespaces);Bobcat.Generated</InterceptorsNamespaces>\n");
        builder.Append("  </PropertyGroup>\n\n");

        if (!centralPackageManagement)
        {
            builder.Append("  <!-- Override either with -p:Name=Version or in a Directory.Build.props. -->\n");
            builder.Append("  <PropertyGroup>\n");
            builder.Append($"    <BobcatVersion Condition=\"'$(BobcatVersion)' == ''\">{bobcatVersion}</BobcatVersion>\n");
            builder.Append($"    <WolverineFxBobcatVersion Condition=\"'$(WolverineFxBobcatVersion)' == ''\">{wolverineFxBobcatVersion}</WolverineFxBobcatVersion>\n");
            builder.Append("  </PropertyGroup>\n\n");
        }

        builder.Append("  <ItemGroup>\n");
        foreach (var (id, version, isPrivate) in Packages("$(BobcatVersion)", "$(WolverineFxBobcatVersion)"))
        {
            var versionAttribute = centralPackageManagement ? "" : $" Version=\"{version}\"";
            if (isPrivate)
            {
                builder.Append($"    <PackageReference Include=\"{id}\"{versionAttribute}>\n");
                builder.Append(id == "Bobcat.Generators"
                    ? "      <IncludeAssets>analyzers; build; buildtransitive</IncludeAssets>\n"
                    : "      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>\n");
                builder.Append("      <PrivateAssets>all</PrivateAssets>\n");
                builder.Append("    </PackageReference>\n");
            }
            else
            {
                builder.Append($"    <PackageReference Include=\"{id}\"{versionAttribute} />\n");
            }
        }

        builder.Append("  </ItemGroup>\n\n");
        builder.Append("  <ItemGroup>\n");
        if (appProject is null)
        {
            builder.Append("    <!-- TODO: the application project. no single .csproj was found beside the stubs (the app-project-not-found callout). -->\n");
            builder.Append("    <!-- <ProjectReference Include=\"..\\App\\App.csproj\" /> -->\n");
        }
        else
        {
            builder.Append($"    <ProjectReference Include=\"{relative(specsDirectory, appProject).Replace('/', '\\')}\" />\n");
        }

        builder.Append("  </ItemGroup>\n\n");
        builder.Append("</Project>\n");
        return builder.ToString();
    }

    /// <summary>The global.json that puts <c>dotnet test</c> on Microsoft.Testing.Platform.</summary>
    public const string GlobalJson = "{\n  \"test\": {\n    \"runner\": \"Microsoft.Testing.Platform\"\n  }\n}\n";

    /// <summary><c>AssemblyAttributes.cs</c>: the model name, serial execution and the application's content root.</summary>
    public static string WriteAssemblyAttributes(string modelName, string? appProject, string specsDirectory)
    {
        var builder = new StringBuilder();
        builder.Append("// Written by `write-specs` (WolverineFx.Bobcat.EventModel). Nothing regenerates this file.\n\n");
        builder.Append("using Bobcat;\n");
        if (appProject is not null) builder.Append("using Microsoft.AspNetCore.Mvc.Testing;\n");
        builder.Append('\n');
        builder.Append("// The model these specifications contribute slices to. Event Model sources merge by model name, and the\n");
        builder.Append("// application's is its Wolverine ServiceName, so this must say the same or the slices float off on their own.\n");
        builder.Append($"[assembly: EventModelName(\"{escape(modelName)}\")]\n\n");
        builder.Append("// Every specification resets the event store before it runs, so they run one at a time.\n");
        builder.Append("[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]\n");

        if (appProject is not null)
        {
            var assemblyName = assemblyNameOf(appProject);
            var fromBin = "../../../" + relative(specsDirectory, Path.GetDirectoryName(appProject)!);
            builder.Append('\n');
            builder.Append("// Where the application's content root is, for Alba's WebApplicationFactory: relative to this project's\n");
            builder.Append("// bin/<configuration>/<framework>/ folder, and used only when the marker file is really there, so a\n");
            builder.Append("// different output layout falls back to the factory's own search.\n");
            builder.Append($"[assembly: WebApplicationFactoryContentRoot(\"{escape(assemblyName)}\", \"{fromBin.TrimEnd('/')}\", \"{Path.GetFileName(appProject)}\", \"1\")]\n");
        }

        return builder.ToString();
    }

    private static void planGlobalJson(string specs, List<SpecProjectFile> files, List<SpecProjectFile> kept, List<ImportCallout> callouts,
        List<string> report)
    {
        var existing = findUp(specs, GlobalJsonFile);
        if (existing is not null)
        {
            kept.Add(new SpecProjectFile(existing, ""));
            if (!File.ReadAllText(existing).Contains("Microsoft.Testing.Platform", StringComparison.Ordinal))
            {
                callouts.Add(projectCallout(ImportCalloutCodes.GlobalJsonMissingTestRunner, existing,
                    $"{existing} governs the spec project and does not put `dotnet test` on Microsoft.Testing.Platform, which "
                    + "xunit.v3 4.x needs on the .NET 10 SDK. Add the \"test\" section to it.",
                    "\"test\": { \"runner\": \"Microsoft.Testing.Platform\" }"));
            }

            return;
        }

        var root = solutionRoot(specs) ?? specs;
        var path = Path.Combine(root, GlobalJsonFile);
        files.Add(new SpecProjectFile(path, GlobalJson));
        report.Add($"global.json: {path}, so `dotnet test` runs on Microsoft.Testing.Platform from there and below.");
    }

    private static void checkExistingProject(string csproj, string specs, string modelName, List<ImportCallout> callouts)
    {
        var text = File.ReadAllText(csproj);
        foreach (var id in new[] { "xunit.v3", "Bobcat.Xunit", "Bobcat.Generators", "WolverineFx.Bobcat", "Alba" })
        {
            if (!Regex.IsMatch(text, $"Include=\"{Regex.Escape(id)}\"", RegexOptions.IgnoreCase))
            {
                // A ProjectReference to the source (Wolverine.Bobcat, Bobcat.Xunit) counts as the package
                var project = id.Replace("WolverineFx.", "Wolverine.");
                if (text.Contains(project + ".csproj", StringComparison.OrdinalIgnoreCase)) continue;
                callouts.Add(projectCallout(ImportCalloutCodes.SpecProjectMissingReference, csproj,
                    $"The spec project references no {id}, which the generated specifications need.",
                    id == "Bobcat.Generators"
                        ? "<PackageReference Include=\"Bobcat.Generators\" PrivateAssets=\"all\" />"
                        : $"<PackageReference Include=\"{id}\" />"));
            }
        }

        var declaresName = sourceFiles(specs, null).Any(x => File.ReadAllText(x).Contains("EventModelName", StringComparison.Ordinal));
        if (!declaresName)
        {
            callouts.Add(projectCallout(ImportCalloutCodes.SpecProjectMissingEventModelName, csproj,
                "The spec project declares no [assembly: EventModelName], so its slices are named for the test assembly and never "
                + "join the application's model.", $"[assembly: EventModelName(\"{escape(modelName)}\")]"));
        }
    }

    private static void checkSolution(string specs, string csproj, List<ImportCallout> callouts)
    {
        var root = solutionRoot(specs, gitFallback: false);
        if (root is null) return;
        var solutions = Directory.GetFiles(root, "*.sln").Concat(Directory.GetFiles(root, "*.slnx")).ToList();
        var name = Path.GetFileName(csproj);
        if (solutions.Any(x => File.ReadAllText(x).Contains(name, StringComparison.OrdinalIgnoreCase))) return;

        var solution = solutions[0];
        callouts.Add(projectCallout(ImportCalloutCodes.SpecProjectNotInSolution, solution,
            $"The spec project is not in {Path.GetFileName(solution)}, so building the solution does not build the specifications.",
            $"dotnet sln \"{solution}\" add \"{csproj}\""));
    }

    private static ImportCallout projectCallout(string code, string? file, string message, string? fix)
        => new("", code, [], message, null) { File = file, Fix = fix };

    private static readonly Regex publicProgram = new(@"public\s+(?:sealed\s+)?(?:partial\s+)?class\s+Program\b", RegexOptions.Compiled);

    private static IEnumerable<string> sourceFiles(string directory, string? excluded)
    {
        if (!Directory.Exists(directory)) yield break;
        var skip = excluded is null ? null : Path.GetFullPath(excluded).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            var parts = Path.GetRelativePath(directory, full).Split(Path.DirectorySeparatorChar);
            if (parts.Any(x => x is "bin" or "obj")) continue;
            if (skip is not null && full.StartsWith(skip, StringComparison.Ordinal)) continue;
            yield return full;
        }
    }

    private static string? findUp(string start, string fileName)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>The nearest directory at or above <paramref name="start"/> with a .sln or .slnx, else the git root.</summary>
    private static string? solutionRoot(string start, bool gitFallback = true)
    {
        string? git = null;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists) continue;
            if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any()) return directory.FullName;
            if (git is null && (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))))
            {
                git = directory.FullName;
                break;
            }
        }

        return gitFallback ? git : null;
    }

    private static bool isCentral(string props)
        => Regex.IsMatch(props, @"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase);

    private static HashSet<string> declaredPackageVersions(string props)
        => Regex.Matches(props, "<PackageVersion\\s+Include=\"([^\"]+)\"", RegexOptions.IgnoreCase)
            .Select(x => x.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The application's WolverineFx version: off a WolverineFx package in its csproj, else its Directory.Packages.props.</summary>
    private static string? wolverineVersionOf(string appProject)
    {
        var pattern = new Regex("Include=\"WolverineFx(?:\\.[\\w.]+)?\"\\s+Version=\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var match = pattern.Match(File.ReadAllText(appProject));
        if (match.Success) return match.Groups[1].Value;
        var props = findUp(Path.GetDirectoryName(appProject)!, "Directory.Packages.props");
        if (props is null) return null;
        match = pattern.Match(File.ReadAllText(props));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string targetFrameworkOf(string csproj)
    {
        var match = Regex.Match(csproj, @"<TargetFrameworks?>\s*([^<]+?)\s*</TargetFrameworks?>");
        if (!match.Success) return "net10.0";
        return match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => Regex.IsMatch(x, @"^net\d+\.\d+$"))
            .OrderByDescending(x => Version.Parse(x[3..]))
            .FirstOrDefault() ?? "net10.0";
    }

    private static string assemblyNameOf(string appProject)
    {
        var match = Regex.Match(File.ReadAllText(appProject), @"<AssemblyName>\s*([^<]+?)\s*</AssemblyName>");
        return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(appProject);
    }

    private static string relative(string from, string to) => Path.GetRelativePath(from, to).Replace('\\', '/');

    private static string escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
