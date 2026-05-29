using System.Diagnostics;
using Shouldly;
using Wolverine.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Http.FSharpTests;

/// <summary>
///     The Phase C acceptance gate (issue GH-2969): regenerates the fixture's <c>Generated.fs</c> from
///     real Wolverine.Http endpoint chains, then shells <c>dotnet build</c> on the checked-in F# fixture
///     and asserts a clean build. Mirrors the Core surface's compile gate.
/// </summary>
public class HttpFSharpCompileGate
{
    private readonly ITestOutputHelper _output;

    public HttpFSharpCompileGate(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void generated_fsharp_compiles_via_dotnet_build()
    {
        var code = HttpFSharpCodegenSample.GenerateCode();
        var generatedFile = HttpFSharpCodegenSample.DefaultGeneratedFilePath();
        File.WriteAllText(generatedFile, code);

        File.Exists(generatedFile).ShouldBeTrue();
        _output.WriteLine(code);

        var fixtureProject = HttpFSharpCodegenSample.FixtureProjectPath();
        var (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");

        // Retry once on the transient FS0193 internal-compiler crash (see the Core surface gate).
        if (exitCode != 0 && (output.Contains("FS0193") || output.Contains("internal error")))
        {
            (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");
        }

        _output.WriteLine(output);
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task fsharp_coverage_includes_http_frames()
    {
        // Run from this assembly so the loaded Wolverine.* set includes Wolverine.Http; surfaces the
        // HTTP frame coverage tally (implemented / skipped / remaining) in CI test output.
        var command = new WolverineDiagnosticsCommand();
        var result = await command.Execute(new WolverineDiagnosticsInput { Action = "fsharp-coverage" });
        result.ShouldBeTrue();
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
