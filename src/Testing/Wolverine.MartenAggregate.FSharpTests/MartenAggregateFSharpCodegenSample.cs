using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;
using WolverineMartenAggregateFSharpSample;

namespace Wolverine.MartenAggregate.FSharpTests;

/// <summary>
///     Renders the sample's Marten event-sourced aggregate handler chain (issue GH-2969) as F#. Builds
///     a minimal host that discovers <see cref="IncrementHandler" /> with Marten integration + the
///     Counter projection + auto-applied transactions, compiles the handler graph without starting it
///     (no DB connection, no Roslyn), and emits the adapter as F# via
///     <see cref="GeneratedAssembly.GenerateFSharpCode" /> — exercising the aggregate frames
///     (FetchForWriting load, missing-aggregate guard, otel tagging, RegisterEvents, SaveChanges).
/// </summary>
public static class MartenAggregateFSharpCodegenSample
{
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
                    // No projection registration needed: codegen only emits the FetchForWriting<Counter>
                    // call; the F# projection's convention Apply/Create can't be source-gen-dispatched
                    // (the JasperFx.Events generator is C#-only) and isn't required to render the chain.
                    opts.Services.AddMarten(m => m.Connection(ConnectionString))
                        .IntegrateWithWolverine();

                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<IncrementHandler>();
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host (no Roslyn, no DB connection).
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var chain = handlerGraph.ChainFor(typeof(IncrementCounter))
                        ?? throw new InvalidOperationException("No handler chain was built for IncrementCounter.");

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
        return Path.Combine(srcTestingDir, "Wolverine.MartenAggregate.FSharpFixture", "Generated.fs");
    }

    public static string FixtureProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.MartenAggregate.FSharpFixture",
            "Wolverine.MartenAggregate.FSharpFixture.fsproj");
    }
}
