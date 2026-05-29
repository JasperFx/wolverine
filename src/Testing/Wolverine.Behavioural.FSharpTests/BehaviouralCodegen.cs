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
}
