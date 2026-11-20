using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

// GH-5703. In 7.0 an application in the Production profile loads pre-generated code, so MissingPreBuiltTypesException
// is the first thing a plain `dotnet run` of a 7.0 app can meet. Wherever it is thrown -- message handlers and saga
// chains here, the Marten, Polecat and Fisher chains (handler chains too), HTTP endpoints and gRPC services in their own
// test suites -- the message names the missing types and ends with what to do about it
public class missing_pre_built_types_say_how_to_fix_5703
{
    [Fact]
    public void the_exception_always_ends_with_how_to_fix()
    {
        var ex = new MissingPreBuiltTypesException("Two pre-built types are missing");

        ex.Message.ShouldStartWith("Two pre-built types are missing");
        ex.Message.ShouldEndWith(MissingPreBuiltTypesException.HowToFix);
    }

    [Fact]
    public void how_to_fix_is_never_said_twice()
    {
        var ex = new MissingPreBuiltTypesException("Missing\n\n" + MissingPreBuiltTypesException.HowToFix);

        ex.Message.IndexOf(MissingPreBuiltTypesException.HowToFix, StringComparison.Ordinal)
            .ShouldBe(ex.Message.LastIndexOf(MissingPreBuiltTypesException.HowToFix, StringComparison.Ordinal));
    }

    [Fact]
    public void how_to_fix_names_both_ways_out()
    {
        MissingPreBuiltTypesException.HowToFix.ShouldContain("dotnet run -- codegen write");
        MissingPreBuiltTypesException.HowToFix.ShouldContain("RestoreV6Defaults()");
        MissingPreBuiltTypesException.HowToFix.ShouldContain("x.Production.GeneratedCodeMode = TypeLoadMode.Auto");
    }

    [Fact]
    public async Task a_handler_and_a_saga_without_pre_built_types()
    {
        var ex = await Should.ThrowAsync<MissingPreBuiltTypesException>(() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ShipParcel5703Handler))
                    .IncludeType<Delivery5703>();
            }).StartAsync(TestContext.Current.CancellationToken));

        // The missing types, by the messages their chains handle
        ex.Message.ShouldContain(nameof(ShipParcel5703));
        ex.Message.ShouldContain(nameof(StartDelivery5703));
        ex.Message.ShouldContain(nameof(Delivery5703));

        ex.Message.ShouldEndWith(MissingPreBuiltTypesException.HowToFix);
    }
}

public record ShipParcel5703(Guid Id);

[WolverineIgnore]
public static class ShipParcel5703Handler
{
    public static void Handle(ShipParcel5703 command)
    {
    }
}

public record StartDelivery5703(Guid Id);

[WolverineIgnore]
public class Delivery5703 : Saga
{
    public Guid Id { get; set; }

    public static Delivery5703 Start(StartDelivery5703 command) => new() { Id = command.Id };
}

// GH-5703. A plain `dotnet run` with no launch settings runs in Production, where Wolverine 7.0 expects pre-generated
// code. So every sample application in this repository either ships launch settings that run it in Development, or says
// how it runs its generated code itself (CritterStackDefaults, RestoreV6Defaults or a TypeLoadMode)
public class every_sample_says_how_it_runs_its_generated_code_5703
{
    [Fact]
    public void every_sample_ships_development_launch_settings_or_sets_the_code_mode()
    {
        var src = Path.Combine(repositoryRoot(), "src");
        var samples = Directory.EnumerateFiles(src, "Program.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(startsAWolverineHost)
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .ToArray();

        samples.Length.ShouldBeGreaterThan(50);

        var silent = samples.Where(x => !runsInDevelopment(x) && !setsTheCodeMode(x))
            .Select(x => Path.GetRelativePath(src, x))
            .Order(StringComparer.Ordinal)
            .ToArray();

        silent.ShouldBeEmpty("These samples would fail a plain `dotnet run` with MissingPreBuiltTypesException");
    }

    // A comment that mentions UseWolverine doesn't make an application of it
    private static bool startsAWolverineHost(string program)
        => File.ReadLines(program).Select(x => x.Trim()).Where(x => !x.StartsWith("//"))
            .Any(x => x.Contains("UseWolverine") || x.Contains("AddWolverine"));

    private static bool runsInDevelopment(string directory)
    {
        var launchSettings = Path.Combine(directory, "Properties", "launchSettings.json");
        return File.Exists(launchSettings) && File.ReadAllText(launchSettings).Contains("\"Development\"");
    }

    private static bool setsTheCodeMode(string directory)
        => Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .Any(x => x.Contains("GeneratedCodeMode") || x.Contains("RestoreV6Defaults") || x.Contains("TypeLoadMode."));

    private static string repositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "wolverine.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}
