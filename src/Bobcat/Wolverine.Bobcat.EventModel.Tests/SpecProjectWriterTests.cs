using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Bobcat.EventModel;
using Bobcat.EventModel.Emlang;
using Shouldly;

namespace Wolverine.Bobcat.EventModel.Tests;

/// <summary>
/// <c>write-specs</c> (<c>bobcat import-event-model --specs</c> before GH-5082) writes the spec project around the specifications when there is
/// none, never overwrites one, and checks the application without editing it.
/// </summary>
public sealed class SpecProjectWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bobcat-spec-project-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a build server can still hold a file; the temp folder is the OS's to clear
        }
    }

    private string write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string AppCsproj =
        """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="WolverineFx.Http" Version="6.50.0-local.1" />
          </ItemGroup>
        </Project>
        """;

    private static readonly string ReadyCsproj = AppCsproj.Replace("</ItemGroup>",
        "<PackageReference Include=\"WolverineFx.RuntimeCompilation\" Version=\"6.50.0-local.1\" /></ItemGroup>");

    private const string BareProgram =
        """
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        app.Run();
        """;

    private const string ReadyProgram =
        """
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        return await app.RunJasperFxCommands(args);

        public partial class Program;
        """;

    private string app(string program = BareProgram, string csproj = AppCsproj)
    {
        write("Shelter/Program.cs", program);
        return write("Shelter/Shelter.csproj", csproj);
    }

    private string specs => Path.Combine(_root, "Shelter.Specs");

    private SpecProjectPlan plan(string? appProject, string? wolverine = null, string? bobcat = null)
        => SpecProjectWriter.Plan(new SpecProjectRequest(specs, "Shelter.Specs", "Shelter", appProject, bobcat, wolverine));

    private static string contentOf(SpecProjectPlan plan, string fileName)
        => plan.Files.Single(x => Path.GetFileName(x.Path) == fileName).Content;

    private static IReadOnlyList<string> codes(SpecProjectPlan plan) => plan.Callouts.Select(x => x.Code).ToList();

    [Fact]
    public void a_missing_spec_project_is_written_with_everything_the_specifications_need()
    {
        var result = plan(app(ReadyProgram, ReadyCsproj));

        result.Files.Select(x => Path.GetRelativePath(_root, x.Path).Replace('\\', '/')).ShouldBe(
            ["Shelter.Specs/Shelter.Specs.csproj", "Shelter.Specs/AssemblyAttributes.cs", "Shelter.Specs/global.json"],
            ignoreOrder: true);

        // It has to parse: an XML comment holding the flag's double hyphen did not (MSB4025)
        var csproj = XDocument.Parse(contentOf(result, "Shelter.Specs.csproj"));
        var packages = csproj.Descendants("PackageReference").ToDictionary(x => x.Attribute("Include")!.Value, x => x.Attribute("Version")?.Value);
        packages.Keys.ShouldBe(
            ["xunit.v3", "Bobcat", "Bobcat.Xunit", "Bobcat.Generators", "WolverineFx.Bobcat", "Alba", "Microsoft.NET.Test.Sdk", "xunit.runner.visualstudio"],
            ignoreOrder: true);
        packages["xunit.v3"].ShouldBe("4.0.2");
        packages["Bobcat"].ShouldBe("$(BobcatVersion)");
        packages["WolverineFx.Bobcat"].ShouldBe("$(WolverineFxBobcatVersion)");

        var generators = csproj.Descendants("PackageReference").Single(x => x.Attribute("Include")!.Value == "Bobcat.Generators");
        generators.Element("PrivateAssets")!.Value.ShouldBe("all");
        generators.Element("IncludeAssets")!.Value.ShouldContain("analyzers");

        string property(string name) => csproj.Descendants(name).Single().Value;
        property("OutputType").ShouldBe("Exe");
        property("TargetFramework").ShouldBe("net10.0");
        property("UseMicrosoftTestingPlatformRunner").ShouldBe("true");
        property("InterceptorsNamespaces").ShouldContain("Bobcat.Generated");
        property("BobcatVersion").ShouldBe(SpecProjectWriter.BobcatVersion);
        property("WolverineFxBobcatVersion").ShouldBe(SpecProjectWriter.DefaultWolverineFxBobcatVersion);

        csproj.Descendants("ProjectReference").Single().Attribute("Include")!.Value.ShouldBe(@"..\Shelter\Shelter.csproj");

        JsonDocument.Parse(contentOf(result, "global.json")).RootElement
            .GetProperty("test").GetProperty("runner").GetString().ShouldBe("Microsoft.Testing.Platform");

        var attributes = contentOf(result, SpecProjectWriter.AssemblyAttributesFile);
        attributes.ShouldContain("[assembly: EventModelName(\"Shelter\")]");
        attributes.ShouldContain("Parallelization(Mode = Xunit.Sdk.ParallelMode.None)");
        attributes.ShouldContain("[assembly: WebApplicationFactoryContentRoot(\"Shelter\", \"../../../../Shelter\", \"Shelter.csproj\", \"1\")]");

        result.Callouts.ShouldBeEmpty();
    }

    [Fact]
    public void the_tool_version_is_the_assemblys_own()
    {
        // The package version, prerelease suffix included when it has one: a 6.54.0-alpha.1 tool pinned its
        // spec project to 6.54.0-alpha.1, because no 6.54.0 of the package existed to restore.
        var assembly = typeof(SpecProjectWriter).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        SpecProjectWriter.ToolVersion.ShouldBe(informational.Split('+')[0]);
        SpecProjectWriter.ToolVersion.ShouldStartWith(assembly.GetName().Version!.ToString(3));
    }

    [Fact]
    public void the_default_wolverinefx_bobcat_version_is_the_stamped_one()
    {
        // GH-5222: stamped by the build from WolverineFx.Bobcat's own version property, not this package's version
        var stamped = typeof(SpecProjectWriter).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "WolverineFxBobcatVersion").Value;

        stamped.ShouldNotBeNullOrEmpty();
        SpecProjectWriter.DefaultWolverineFxBobcatVersion.ShouldBe(stamped);
    }

    /// <summary>
    /// GH-5222: the invariant itself. A spec project written with no <c>--wolverine-version</c> pins the stamped
    /// version, so it has to be the version <c>dotnet pack</c> gives WolverineFx.Bobcat in this same tree, read off
    /// the project's evaluated <c>PackageVersion</c> rather than off anything this package declares. While the
    /// default was this package's own version, the two agreed only because both carried <c>-alpha.1</c>.
    /// </summary>
    [Fact]
    public async Task the_default_wolverinefx_bobcat_version_is_what_wolverinefx_bobcat_packs_as()
    {
        var project = Path.Combine(repositoryRoot(), "src", "Bobcat", "Wolverine.Bobcat", "Wolverine.Bobcat.csproj");
        File.Exists(project).ShouldBeTrue(project);

        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "msbuild", project, "-getProperty:PackageId", "-getProperty:PackageVersion" })
        {
            info.ArgumentList.Add(argument);
        }

        // The test host can inherit the outer build's MSBuild location variables, which would evaluate the
        // project with a different MSBuild than the SDK global.json selects
        foreach (var key in info.Environment.Keys.Where(x => x.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            info.Environment.Remove(key);
        }

        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var evaluation = Process.Start(info)!;
        var output = await evaluation.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await evaluation.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await evaluation.WaitForExitAsync(TestContext.Current.CancellationToken);
        evaluation.ExitCode.ShouldBe(0, output + error);

        var properties = JsonDocument.Parse(output).RootElement.GetProperty("Properties");
        properties.GetProperty("PackageId").GetString().ShouldBe("WolverineFx.Bobcat");
        var packed = properties.GetProperty("PackageVersion").GetString();
        packed.ShouldNotBeNullOrEmpty();

        SpecProjectWriter.DefaultWolverineFxBobcatVersion.ShouldBe(packed);
    }

    private static string repositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "wolverine.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException($"No wolverine.slnx above {AppContext.BaseDirectory}: this test reads WolverineFx.Bobcat's project from the source tree it was built in.");
    }

    [Fact]
    public void the_bobcat_version_is_the_one_this_package_was_built_against()
    {
        // GH-5082: stamped from the repository's central Bobcat version, which is what WolverineFx.Bobcat references
        var stamped = typeof(SpecProjectWriter).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "BobcatVersion").Value;

        stamped.ShouldNotBeNullOrEmpty();
        SpecProjectWriter.BobcatVersion.ShouldBe(stamped);
        SpecProjectWriter.BobcatVersion.ShouldBe(typeof(global::Bobcat.Monitoring.MonitorEvent).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);
    }

    [Fact]
    public void the_versions_can_be_overridden()
    {
        var csproj = XDocument.Parse(contentOf(plan(app(ReadyProgram), wolverine: "6.50.0-local.1", bobcat: "0.61.0-local.2"), "Shelter.Specs.csproj"));

        csproj.Descendants("WolverineFxBobcatVersion").Single().Value.ShouldBe("6.50.0-local.1");
        csproj.Descendants("BobcatVersion").Single().Value.ShouldBe("0.61.0-local.2");

        // A property the build already has (-p: or a Directory.Build.props) still wins
        csproj.Descendants("WolverineFxBobcatVersion").Single().Attribute("Condition")!.Value
            .ShouldBe("'$(WolverineFxBobcatVersion)' == ''");
    }

    [Fact]
    public void the_target_framework_follows_the_application()
    {
        var multi = AppCsproj.Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net9.0;net10.0</TargetFrameworks>");
        var csproj = contentOf(plan(app(ReadyProgram, multi)), "Shelter.Specs.csproj");
        XDocument.Parse(csproj).Descendants("TargetFramework").Single().Value.ShouldBe("net10.0");

        var net9 = contentOf(plan(app(ReadyProgram, AppCsproj.Replace("net10.0", "net9.0"))), "Shelter.Specs.csproj");
        XDocument.Parse(net9).Descendants("TargetFramework").Single().Value.ShouldBe("net9.0");
    }

    [Fact]
    public void an_existing_spec_project_is_never_overwritten_and_is_checked_instead()
    {
        var existing = write("Shelter.Specs/Shelter.Specs.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit.v3" Version="4.0.2" />
                <PackageReference Include="Bobcat.Xunit" Version="0.60.0" />
              </ItemGroup>
            </Project>
            """);
        write("global.json", SpecProjectWriter.GlobalJson);

        var result = plan(app(ReadyProgram));

        result.Files.ShouldBeEmpty();
        result.Kept.Select(x => x.Path).ShouldContain(existing);

        var kept = result.Callouts.Single(x => x.Code == ImportCalloutCodes.SpecProjectFileKept);
        kept.File.ShouldBe(existing);
        kept.Slice.ShouldBe("");

        result.Callouts.Where(x => x.Code == ImportCalloutCodes.SpecProjectMissingReference).Select(x => x.Fix).ShouldBe(
        [
            "<PackageReference Include=\"Bobcat.Generators\" PrivateAssets=\"all\" />",
            "<PackageReference Include=\"WolverineFx.Bobcat\" />",
            "<PackageReference Include=\"Alba\" />"
        ]);
        result.Callouts.Single(x => x.Code == ImportCalloutCodes.SpecProjectMissingEventModelName).Fix
            .ShouldBe("[assembly: EventModelName(\"Shelter\")]");
    }

    [Fact]
    public void an_existing_assembly_attributes_file_is_kept()
    {
        var attributes = write("Shelter.Specs/AssemblyAttributes.cs", "// mine");

        var result = plan(app(ReadyProgram));

        result.Files.ShouldNotContain(x => x.Path == attributes);
        result.Callouts.Single(x => x.Code == ImportCalloutCodes.SpecProjectFileKept).File.ShouldBe(attributes);
    }

    [Fact]
    public void global_json_goes_at_the_solution_root()
    {
        write("Shelter.slnx", "<Solution><Project Path=\"Shelter/Shelter.csproj\" /></Solution>");

        var result = plan(app(ReadyProgram));

        result.Files.ShouldContain(x => x.Path == Path.Combine(_root, "global.json"));
        var callout = result.Callouts.Single(x => x.Code == ImportCalloutCodes.SpecProjectNotInSolution);
        callout.Fix.ShouldBe($"dotnet sln \"{Path.Combine(_root, "Shelter.slnx")}\" add \"{Path.Combine(specs, "Shelter.Specs.csproj")}\"");
    }

    [Fact]
    public void an_existing_global_json_is_checked_never_shadowed()
    {
        var existing = write("global.json", """{ "sdk": { "version": "10.0.100" } }""");

        var result = plan(app(ReadyProgram));

        result.Files.ShouldNotContain(x => Path.GetFileName(x.Path) == "global.json");
        var callout = result.Callouts.Single(x => x.Code == ImportCalloutCodes.GlobalJsonMissingTestRunner);
        callout.File.ShouldBe(existing);
        callout.Fix.ShouldBe("\"test\": { \"runner\": \"Microsoft.Testing.Platform\" }");
    }

    [Fact]
    public void central_package_management_takes_the_versions_in_directory_packages_props()
    {
        var props = write("Directory.Packages.props",
            """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="xunit.v3" Version="4.0.2" /></ItemGroup>
            </Project>
            """);

        var result = plan(app(ReadyProgram), wolverine: "6.50.0-local.1");

        var csproj = XDocument.Parse(contentOf(result, "Shelter.Specs.csproj"));
        csproj.Descendants("PackageReference").ShouldAllBe(x => x.Attribute("Version") == null);

        var callout = result.Callouts.Single(x => x.Code == ImportCalloutCodes.CentralPackageVersions);
        callout.File.ShouldBe(props);
        callout.Fix!.ShouldContain("<PackageVersion Include=\"WolverineFx.Bobcat\" Version=\"6.50.0-local.1\" />");
        callout.Fix!.ShouldNotContain("\"xunit.v3\"");
    }

    [Fact]
    public void what_the_application_is_missing_is_called_out_with_the_exact_fix()
    {
        var project = app();

        var result = plan(project);

        codes(result).ShouldBe(
        [
            ImportCalloutCodes.AppProgramNotPublic,
            ImportCalloutCodes.AppMissingRunJasperFxCommands,
            ImportCalloutCodes.AppMissingRuntimeCompilation
        ]);
        result.Callouts[0].Fix.ShouldBe("public partial class Program;");
        result.Callouts[0].File.ShouldBe(Path.Combine(_root, "Shelter", "Program.cs"));
        result.Callouts[1].Fix.ShouldBe("return await app.RunJasperFxCommands(args);");
        result.Callouts[2].Fix.ShouldBe($"dotnet add \"{project}\" package WolverineFx.RuntimeCompilation --version 6.50.0-local.1");

        // and nothing in the application was touched
        File.ReadAllText(Path.Combine(_root, "Shelter", "Program.cs")).ShouldBe(BareProgram);
        File.ReadAllText(project).ShouldBe(AppCsproj);
    }

    [Fact]
    public void an_application_with_everything_has_no_callouts()
    {
        SpecProjectWriter.CheckApp(app(ReadyProgram, ReadyCsproj)).ShouldBeEmpty();
    }

    [Fact]
    public void a_public_program_inside_the_spec_project_does_not_count_for_the_application()
    {
        write("Shelter/Tests/Program.cs", "public partial class Program;");

        SpecProjectWriter.CheckApp(app(), Path.Combine(_root, "Shelter", "Tests")).Select(x => x.Code)
            .ShouldContain(ImportCalloutCodes.AppProgramNotPublic);
    }

    [Fact]
    public void no_application_project_is_called_out_with_the_flag_that_names_one()
    {
        var result = plan(null);

        var callout = result.Callouts.Single(x => x.Code == ImportCalloutCodes.AppProjectNotFound);
        callout.Override.ShouldBe("--app-project <path/to/App.csproj>");
        contentOf(result, "Shelter.Specs.csproj").ShouldContain("TODO: the application project");
        XDocument.Parse(contentOf(result, "Shelter.Specs.csproj")).Descendants("ProjectReference").ShouldBeEmpty();
    }

    [Fact]
    public void the_application_project_is_the_one_csproj_in_out()
    {
        var project = app();
        SpecProjectWriter.FindAppProject(Path.Combine(_root, "Shelter")).ShouldBe(project);

        write("Shelter/Other.csproj", "<Project />");
        SpecProjectWriter.FindAppProject(Path.Combine(_root, "Shelter")).ShouldBeNull();
    }

    [Fact]
    public void the_json_carries_a_project_callouts_file_and_fix()
    {
        var result = plan(app());

        using var json = JsonDocument.Parse(ImportJson.Write("Shelter", result.Callouts, []));
        var first = json.RootElement.GetProperty("callouts")[0];
        first.GetProperty("code").GetString().ShouldBe(ImportCalloutCodes.AppProgramNotPublic);
        first.GetProperty("file").GetString().ShouldEndWith("Program.cs");
        first.GetProperty("fix").GetString().ShouldBe("public partial class Program;");
    }

    /// <summary>
    /// The whole path: import a board, write the stubs, the specifications and the spec project, and
    /// <c>dotnet build</c> it. Needs NuGet and a WolverineFx.Bobcat that pairs with this Bobcat, so it runs
    /// only when <c>BOBCAT_IMPORT_BUILD_WOLVERINE</c> names that version (and <c>BOBCAT_IMPORT_BUILD_FEED</c>
    /// a local feed holding it, when it is not on nuget.org).
    /// </summary>
    [Fact]
    public async Task the_generated_spec_project_builds()
    {
        var wolverine = Environment.GetEnvironmentVariable("BOBCAT_IMPORT_BUILD_WOLVERINE");
        Assert.SkipWhen(string.IsNullOrEmpty(wolverine), "Set BOBCAT_IMPORT_BUILD_WOLVERINE to a WolverineFx version to build the generated spec project");

        var feed = Environment.GetEnvironmentVariable("BOBCAT_IMPORT_BUILD_FEED");
        if (feed is { Length: > 0 })
        {
            write("NuGet.config",
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <configuration>
                   <packageSources>
                     <clear />
                     <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                     <add key="local" value="{feed}" />
                   </packageSources>
                   <packageSourceMapping>
                     <clear />
                     <packageSource key="local"><package pattern="WolverineFx*" /></packageSource>
                     <packageSource key="nuget.org"><package pattern="*" /></packageSource>
                   </packageSourceMapping>
                 </configuration>
                 """);
        }

        var project = app(ReadyProgram.Replace("var builder", "using JasperFx;\nusing Wolverine;\nvar builder")
                .Replace("var app = builder.Build();", "builder.Host.UseWolverine(opts => opts.ServiceName = \"Shelter\");\nvar app = builder.Build();"),
            AppCsproj.Replace("6.50.0-local.1", wolverine!)
                .Replace("</ItemGroup>", $"<PackageReference Include=\"WolverineFx.RuntimeCompilation\" Version=\"{wolverine}\" /></ItemGroup>"));

        var board = EmlangReader.Read(Boards.Shelter);
        var imported = EmlangImport.ToCurated(board, "Shelter", "Shelter", [], []);
        var generatedSpecs = EmlangSpecWriter.Write(imported.Board ?? board, imported.Model, "Shelter", "Shelter.Specs");
        var model = CSharpModelWriter.Write(imported.Model, "Shelter", generatedSpecs.Additions, perChapter: true);

        foreach (var file in model.StubFiles.Concat(model.DefinitionFiles)) write("Shelter/" + file.Path, file.Content);
        foreach (var file in generatedSpecs.Files) write("Shelter.Specs/" + file.Path, file.Content);

        var result = plan(project, wolverine);
        result.Callouts.ShouldBeEmpty();
        foreach (var file in result.Files) write(Path.GetRelativePath(_root, file.Path), file.Content);

        var build = Process.Start(new ProcessStartInfo("dotnet", "build")
        {
            WorkingDirectory = specs,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["MSBUILDDISABLENODEREUSE"] = "1" }
        })!;
        var output = await build.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await build.WaitForExitAsync(TestContext.Current.CancellationToken);

        build.ExitCode.ShouldBe(0, output);
    }
}
