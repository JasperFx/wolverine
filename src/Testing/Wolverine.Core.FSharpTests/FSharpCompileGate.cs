using System.Diagnostics;
using Shouldly;
using Wolverine.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Core.FSharpTests;

/// <summary>
///     The foundation acceptance gate for Wolverine's F# code generation (issue GH-2969): regenerates
///     the fixture's <c>Generated.fs</c> from real Wolverine frames, then shells <c>dotnet build</c> on
///     the checked-in F# fixture and asserts a clean (exit 0) build. This proves the emitted F# actually
///     compiles with the in-box F# compiler — no extra CI tooling. Mirrors JasperFx#384's
///     <c>FSharpCompilationGate</c>.
/// </summary>
public class FSharpCompileGate
{
    private readonly ITestOutputHelper _output;

    public FSharpCompileGate(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void generated_fsharp_compiles_via_dotnet_build()
    {
        // 1. Regenerate Generated.fs into the checked-in fixture, exactly as the driver would.
        var code = FSharpCodegenSample.GenerateCode();
        var generatedFile = FSharpCodegenSample.DefaultGeneratedFilePath();
        File.WriteAllText(generatedFile, code);

        File.Exists(generatedFile).ShouldBeTrue();
        _output.WriteLine(code);

        // 2. Compile the fixture with the F# compiler that ships in the SDK.
        var fixtureProject = FSharpCodegenSample.FixtureProjectPath();
        var (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");

        // A nested `dotnet build` (build-inside-test) can occasionally trip an internal F# compiler
        // crash (e.g. FS0193 in the auto-generated AssemblyAttributes.fs) that has nothing to do with
        // the generated source. Retry once on that specific signature only — a genuine F# error in
        // Generated.fs is deterministic and would persist across the retry, so this can't mask it.
        if (exitCode != 0 && (output.Contains("FS0193") || output.Contains("internal error")))
        {
            (exitCode, output) = RunDotnet($"build \"{fixtureProject}\" -c Debug --nologo");
        }

        _output.WriteLine(output);
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task fsharp_coverage_command_runs_and_reports()
    {
        // Exercises the `wolverine-diagnostics fsharp-coverage` reflection path against the loaded
        // Wolverine assembly. With the foundation in place, MessageContextFrame counts as implemented,
        // so this both smoke-tests the command and surfaces the coverage tally in CI test output.
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
        // A nested `dotnet build` reusing MSBuild server nodes can hang the child; disable it so
        // the build runs (and exits) in-process and we always get a deterministic exit code.
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using var process = Process.Start(info)!;

        // Read both streams concurrently to avoid a deadlock if either child buffer fills.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
