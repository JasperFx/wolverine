using System.Diagnostics;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using WolverineBehaviouralFSharpApp;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Behavioural.FSharpTests;

/// <summary>
///     The F# behavioural run-step (issue GH-2969): boots a real Wolverine host in
///     <see cref="TypeLoadMode.Static" /> against the Wolverine.Behavioural.FSharpApp assembly (which
///     carries the committed, pre-generated F# handler adapter), sends a message, and asserts the F#
///     handler actually executed — run-verification, not just compilation.
/// </summary>
public class BehaviouralRunStep
{
    private readonly ITestOutputHelper _output;

    public BehaviouralRunStep(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task generated_fsharp_handler_runs_under_static_load()
    {
        BehaviouralSink.reset();

        // begin-snippet: sample_fsharp_static_host
        var appAssembly = typeof(BehaviouralPingHandler).Assembly;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Shared with the generation step so the generated type-name hash matches:
                // DisableConventionalDiscovery() + IncludeType<BehaviouralPingHandler>().
                BehaviouralCodegen.Configure(opts);

                // Load the pre-generated F# handler adapter out of the app assembly instead of
                // compiling at runtime. Setting ApplicationAssembly (which cascades to
                // CodeGeneration.ApplicationAssembly) pins the assembly Wolverine scans for pre-built
                // types to the F# app, and TypeLoadMode.Static means no Roslyn at runtime.
                opts.ApplicationAssembly = appAssembly;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
            })
            .StartAsync();

        // The pre-generated F# MessageHandler is loaded by name and executed — no runtime compilation.
        var bus = host.MessageBus();
        await bus.InvokeAsync(new BehaviouralPing(42));
        // end-snippet

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await BehaviouralSink.received().WaitAsync(timeout.Token);

        received.ShouldBe(42);
    }

    /// <summary>
    ///     Generation gate: regenerate the app's <c>Generated.fs</c> from the shared config and
    ///     <c>dotnet build</c> the app, so the committed F# adapter can't silently drift from the
    ///     codegen output (mirrors the per-store compile-gates).
    /// </summary>
    [Fact]
    public async Task generated_fsharp_regenerates_and_compiles()
    {
        var code = BehaviouralCodegen.GenerateCode();
        var generatedFile = BehaviouralCodegen.GeneratedFilePath();
        File.WriteAllText(generatedFile, code);
        _output.WriteLine(code);

        var appProject = BehaviouralCodegen.AppProjectPath();
        var (exitCode, output) = await RunDotnetAsync($"build \"{appProject}\" -c Debug --nologo");

        if (exitCode != 0 && (output.Contains("FS0193") || output.Contains("internal error")
                                                        || output.Contains("being used by another process")
                                                        || output.Contains("MSB3883")))
        {
            (exitCode, output) = await RunDotnetAsync($"build \"{appProject}\" -c Debug --nologo");
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
