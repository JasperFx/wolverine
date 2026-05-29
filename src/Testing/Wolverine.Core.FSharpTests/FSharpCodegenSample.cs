using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Core.FSharpContracts;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Core.FSharpTests;

/// <summary>
///     Renders the Phase A handler chain (issue GH-2969) as F#. Builds a minimal in-memory Wolverine
///     host that discovers <see cref="NameHandler" />, compiles its real handler chain (message
///     extraction → simple validation abort → handler call → cascaded message), and emits the adapter
///     as F# via <see cref="GeneratedAssembly.GenerateFSharpCode" /> — the same path
///     <c>WolverineDiagnosticsCommand.GenerateSingleFileCode</c> uses for C#, swapping
///     <c>GenerateCode</c> for <c>GenerateFSharpCode</c>.
/// </summary>
public static class FSharpCodegenSample
{
    /// <summary>Builds the real handler chain for <see cref="CreateName" /> and renders it as F# source.</summary>
    public static string GenerateCode()
    {
        // Apply lightweight codegen mode so the host stands up without transports / persistence and so
        // resolving the code-file collections compiles the handler graph without starting it.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<NameHandler>();
                    opts.Discovery.IncludeType<CheckThingHandler>();
                    opts.Discovery.IncludeType<GateHandler>();

                    // Inserts ApplyExecutionDiagnosticTagsFrame at the head of every chain.
                    opts.Tracking.HandlerExecutionDiagnosticsEnabled = true;
                })
                .Build();

            // Force HandlerGraph.Compile() to run *without starting the host* (no Roslyn, no
            // transport/persistence connections) — exactly as the describe-handlers command does.
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            // Render every handler chain defined in the contracts assembly into one Generated.fs so the
            // compile gate exercises the whole Phase A frame set (validation, requirement-result, the
            // HandlerContinuation gate, OTel/audit tags, cascading) in one build.
            var contractsAssembly = typeof(CreateName).Assembly;
            var chains = handlerGraph.AllChains()
                .Where(c => c.MessageType.Assembly == contractsAssembly)
                .OrderBy(c => c.MessageType.Name)
                .ToArray();

            if (chains.Length == 0)
            {
                throw new InvalidOperationException("No handler chains were built for the contracts assembly.");
            }

            foreach (var chain in chains)
            {
                ((ICodeFile)chain).AssembleTypes(generatedAssembly);
            }

            return generatedAssembly.GenerateFSharpCode(serviceVariableSource);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    /// <summary>
    ///     The checked-in fixture's <c>Generated.fs</c>, located relative to this source file so it
    ///     resolves regardless of the test runner's working directory or bin layout.
    /// </summary>
    public static string DefaultGeneratedFilePath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Core.FSharpFixture", "Generated.fs");
    }

    public static string FixtureProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Core.FSharpFixture", "Wolverine.Core.FSharpFixture.fsproj");
    }
}
