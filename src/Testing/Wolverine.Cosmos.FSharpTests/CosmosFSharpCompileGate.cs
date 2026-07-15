using System.Diagnostics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Cosmos.FSharpTests;

/// <summary>
///     The FluentValidation + CosmosDB acceptance gate (issue GH-2969): regenerates the fixture's
///     <c>Generated.fs</c> from the sample's combined handler chain, then shells <c>dotnet build</c>
///     on the checked-in F# fixture and asserts a clean build. Mirrors the Core/Http/EfCore/Marten
///     surface gates.
/// </summary>
public class CosmosFSharpCompileGate
{
    private readonly ITestOutputHelper _output;

    public CosmosFSharpCompileGate(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task generated_fsharp_compiles_via_dotnet_build()
    {
        // Both saga storage layouts, because they emit different F#: the partitioned one (GH-3415) reads and
        // deletes the saga in the partition keyed by its id and writes it through CosmosSagaStorage. The
        // default is rendered last so that the checked-in Generated.fs stays the one the fixture describes.
        await assertCompilesAsync(partitionSagasById: true);
        await assertCompilesAsync(partitionSagasById: false);
    }

    private async Task assertCompilesAsync(bool partitionSagasById)
    {
        var code = CosmosFSharpCodegenSample.GenerateCode(partitionSagasById);
        var generatedFile = CosmosFSharpCodegenSample.DefaultGeneratedFilePath();
        File.WriteAllText(generatedFile, code);

        File.Exists(generatedFile).ShouldBeTrue();
        _output.WriteLine($"PartitionSagasById: {partitionSagasById}");
        _output.WriteLine(code);

        var fixtureProject = CosmosFSharpCodegenSample.FixtureProjectPath();
        var (exitCode, output) = await RunDotnetAsync($"build \"{fixtureProject}\" -c Debug --nologo");

        // Retry once on the transient FS0193 internal-compiler crash or a concurrent-build file lock.
        if (exitCode != 0 && (output.Contains("FS0193") || output.Contains("internal error")
                                                        || output.Contains("being used by another process")
                                                        || output.Contains("MSB3883")))
        {
            (exitCode, output) = await RunDotnetAsync($"build \"{fixtureProject}\" -c Debug --nologo");
        }

        _output.WriteLine(output);
        exitCode.ShouldBe(0);
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(string arguments)
    {
        var info = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }
}
