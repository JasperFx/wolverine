using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;
using WolverineMartenFSharpSample;

namespace Wolverine.Marten.FSharpTests;

/// <summary>
///     Renders the sample's Marten document handler chain (issue GH-2969) as F#. Builds a minimal host
///     that discovers <see cref="CreateProductHandler" /> with Marten integration + auto-applied
///     transactions, compiles the handler graph without starting it (no DB connection, no Roslyn), and
///     emits the adapter as F# via <see cref="GeneratedAssembly.GenerateFSharpCode" /> — exercising the
///     Marten document frames (open outbox session, SaveChanges).
/// </summary>
public static class MartenFSharpCodegenSample
{
    // Codegen never opens a connection; the string only has to be well-formed for AddMarten bootstrap.
    private const string ConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres";

    public static string GenerateCode()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddMarten(m => m.Connection(ConnectionString)).IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<CreateProductHandler>();
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host (no Roslyn, no DB connection).
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var chain = handlerGraph.ChainFor(typeof(CreateProductCommand))
                        ?? throw new InvalidOperationException("No handler chain was built for CreateProductCommand.");

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

    public static string DefaultGeneratedFilePath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Marten.FSharpFixture", "Generated.fs");
    }

    public static string FixtureProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Marten.FSharpFixture", "Wolverine.Marten.FSharpFixture.fsproj");
    }
}
