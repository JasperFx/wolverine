using System.Diagnostics;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Handlers;
using WolverineBehaviouralFSharpApp;

namespace Wolverine.Behavioural.FSharpTests;

/// <summary>
///     Shared configuration + F# rendering for the behavioural run-step. <see cref="Configure" /> is used
///     by BOTH the generation step (to emit the F# adapter) and the runtime host (to compute the same
///     chain, hence the same generated type name) so the pre-generated type is found under static load.
/// </summary>
public static class BehaviouralCodegen
{
    /// <summary>
    ///     The handler-discovery configuration shared by generation and runtime. Deliberately minimal +
    ///     deterministic so the generated handler type name is stable.
    /// </summary>
    public static void Configure(WolverineOptions opts)
    {
        opts.Discovery.DisableConventionalDiscovery();
        opts.Discovery.IncludeType<BehaviouralPingHandler>();
    }

    /// <summary>
    ///     Renders the BehaviouralPing chain's handler adapter as F# via the no-host codegen path.
    /// </summary>
    public static string GenerateCode()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(Configure)
                .Build();

            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var chain = handlerGraph.ChainFor(typeof(BehaviouralPing))
                        ?? throw new InvalidOperationException("No handler chain was built for BehaviouralPing.");

            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);
            ((ICodeFile)chain).AssembleTypes(generatedAssembly);

            return generatedAssembly.GenerateFSharpCode(serviceVariableSource);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    public static string GeneratedFilePath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Behavioural.FSharpApp", "Generated.fs");
    }

    public static string AppProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Behavioural.FSharpApp", "Wolverine.Behavioural.FSharpApp.fsproj");
    }

    /// <summary>
    ///     Reads <c>Generated.fs</c> as it is <em>committed</em> to git (<c>HEAD</c>), deliberately NOT as it
    ///     currently sits in the working tree.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="BehaviouralRunStep.generated_fsharp_regenerates_and_compiles" /> rewrites the
    ///         working-tree copy of this very file in place — that is how the committed fixture gets
    ///         refreshed. A comparison that read the file off disk would therefore only ever see the
    ///         committed bytes when it happened to run <em>before</em> that sibling; run after it, the
    ///         comparison is fresh-codegen against fresh-codegen and passes vacuously. Both tests live in
    ///         the same non-parallelized collection, so which one goes first is an xUnit ordering detail,
    ///         which made this gate pass or fail on test order rather than on the thing it exists to check
    ///         (GH-4754).
    ///     </para>
    ///     <para>
    ///         Reading from <c>HEAD</c> removes the ordering dependency entirely, and also closes the
    ///         second-run hole: a working tree left dirty by an earlier local run can no longer launder
    ///         stale drift into a green gate.
    ///     </para>
    /// </remarks>
    public static async Task<string> ReadCommittedGeneratedFileAsync(CancellationToken cancellation)
    {
        var appDir = Path.GetDirectoryName(GeneratedFilePath())!;

        // `<rev>:./<path>` resolves the path relative to the working directory, so this needs no
        // knowledge of where the repository root is. Works in a git worktree too.
        var (exitCode, stdout, stderr) =
            await runGitAsync(appDir, "show HEAD:./Generated.fs", cancellation);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not read the committed Generated.fs from git (exit {exitCode}). This gate compares the "
                + "CLI's F# output against the COMMITTED fixture on purpose — see "
                + $"{nameof(ReadCommittedGeneratedFileAsync)}. If Generated.fs is newly added, commit it first."
                + Environment.NewLine + stderr);
        }

        return stdout;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> runGitAsync(
        string workingDirectory, string arguments, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = await process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);

        return (process.ExitCode, stdout, stderr);
    }
}
