using System.Diagnostics;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Behavioural.FSharpTests;

// The behavioural F# tests each spawn nested `dotnet build`/`dotnet run` of the F# app and load its
// assembly; running them concurrently races on the app's build outputs. Serialize them.
[CollectionDefinition("BehaviouralFSharp", DisableParallelization = true)]
public class BehaviouralFSharpCollection;

/// <summary>
///     Proves the JasperFx <c>codegen write --language fsharp</c> CLI flag works end to end against a
///     real Wolverine F# application: it runs the verb against Wolverine.Behavioural.FSharpApp,
///     asserts every handler chain (including Wolverine's internal static HandlerRegistry) emits F#
///     (exit 0), and confirms the generated handler adapter is identical to the committed
///     <c>Generated.fs</c> — which the behavioural run-step already compiles and executes under
///     <see cref="JasperFx.CodeGeneration.TypeLoadMode.Static" />. So: the CLI produces the same F#
///     that is proven to run.
/// </summary>
[Collection("BehaviouralFSharp")]
public class CodegenWriteFSharpCli
{
    private readonly ITestOutputHelper _output;

    public CodegenWriteFSharpCli(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task codegen_write_fsharp_generates_runnable_fsharp_for_a_wolverine_app()
    {
        var appProject = BehaviouralCodegen.AppProjectPath();
        var appDir = Path.GetDirectoryName(appProject)!;
        var generatedDir = Path.Combine(appDir, "Internal");

        // Start clean so we only see what THIS run produced.
        if (Directory.Exists(generatedDir)) Directory.Delete(generatedDir, recursive: true);

        try
        {
            var (exitCode, output) = await RunDotnetAsync(appDir, "run --framework net9.0 -- codegen write --language fsharp");

            // A transient F# build crash / file lock can fail the embedded `dotnet run` build; retry once.
            if (exitCode != 0 && (output.Contains("FS0193") || output.Contains("internal error")
                                                            || output.Contains("being used by another process")
                                                            || output.Contains("MSB3883")))
            {
                if (Directory.Exists(generatedDir)) Directory.Delete(generatedDir, recursive: true);
                (exitCode, output) = await RunDotnetAsync(appDir, "run --framework net9.0 -- codegen write --language fsharp");
            }

            _output.WriteLine(output);

            // Exit 0 means every generated handler chain — including Wolverine's internal static
            // HandlerRegistry (WriteTypeArrayFrame) — successfully emitted F#.
            exitCode.ShouldBe(0);

            var generatedFiles = Directory.GetFiles(generatedDir, "*.fs", SearchOption.AllDirectories);
            generatedFiles.ShouldNotBeEmpty();

            // The handler adapter the CLI produced must match the committed Generated.fs that the
            // behavioural run-step compiles + executes under TypeLoadMode.Static.
            var adapterFile = generatedFiles.Single(f =>
                Path.GetFileName(f).StartsWith("BehaviouralPingHandler", StringComparison.Ordinal));
            var generatedAdapter = Normalize(await File.ReadAllTextAsync(adapterFile));
            var committedAdapter = Normalize(await File.ReadAllTextAsync(BehaviouralCodegen.GeneratedFilePath()));
            generatedAdapter.ShouldBe(committedAdapter);

            // The static HandlerRegistry was also emitted as valid F# (the Type[] accessors as F#
            // array literals) — this is what previously threw NotSupportedException.
            var registryFile = generatedFiles.Single(f =>
                Path.GetFileName(f) == "GeneratedHandlerRegistry.fs");
            var registry = await File.ReadAllTextAsync(registryFile);
            registry.ShouldContain("inherit Wolverine.Runtime.Handlers.HandlerRegistry()");
            registry.ShouldContain("typeof<WolverineBehaviouralFSharpApp.BehaviouralPingHandler>");
        }
        finally
        {
            if (Directory.Exists(generatedDir)) Directory.Delete(generatedDir, recursive: true);
        }
    }

    private static string Normalize(string code)
        => code.Replace("\r\n", "\n").Trim();

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(string workingDirectory, string arguments)
    {
        var info = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
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
