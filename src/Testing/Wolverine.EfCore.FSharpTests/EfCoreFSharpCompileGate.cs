using System.Diagnostics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.EfCore.FSharpTests;

/// <summary>
///     The EF Core acceptance gate (issue GH-2969): regenerates the fixture's <c>Generated.fs</c> from
///     the sample's EF Core handler chain, then shells <c>dotnet build</c> on the checked-in F# fixture
///     and asserts a clean build. Mirrors the Core/Http surface gates.
/// </summary>
public class EfCoreFSharpCompileGate
{
    private readonly ITestOutputHelper _output;

    public EfCoreFSharpCompileGate(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void generated_fsharp_compiles_via_dotnet_build()
    {
        var code = EfCoreFSharpCodegenSample.GenerateCode();
        var generatedFile = EfCoreFSharpCodegenSample.DefaultGeneratedFilePath();
        File.WriteAllText(generatedFile, code);

        File.Exists(generatedFile).ShouldBeTrue();
        _output.WriteLine(code);

        var fixtureProject = EfCoreFSharpCodegenSample.FixtureProjectPath();
        var (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");

        // Retry once on the transient FS0193 internal-compiler crash or a concurrent-build file lock.
        if (exitCode != 0 && (output.Contains("FS0193") || output.Contains("internal error")
                                                        || output.Contains("being used by another process")
                                                        || output.Contains("MSB3883")))
        {
            (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");
        }

        _output.WriteLine(output);
        exitCode.ShouldBe(0);
    }

    private static (int ExitCode, string Output) RunDotnet(string arguments)
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
