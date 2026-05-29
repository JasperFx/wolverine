using System.Runtime.CompilerServices;
using FluentValidation;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.FluentValidation;
using Wolverine.Runtime.Handlers;
using WolverineCosmosFSharpSample;

namespace Wolverine.Cosmos.FSharpTests;

/// <summary>
///     Renders the sample's combined FluentValidation + CosmosDB handler chain (issue GH-2969) as F#.
///     Builds a minimal host that discovers <see cref="CreateThingHandler" /> with the FluentValidation
///     policy + CosmosDB persistence + auto-applied transactions, compiles the handler graph without
///     starting it (no Cosmos connection, no Roslyn), and emits the adapter as F# via
///     <see cref="GeneratedAssembly.GenerateFSharpCode" /> — exercising the FluentValidation ExecuteOne
///     MethodCall + the CosmosDB TransactionalFrame + the ICosmosDbOp storage-action applier.
/// </summary>
public static class CosmosFSharpCodegenSample
{
    // The emulator endpoint/key; CosmosClient is constructed lazily and never connects during codegen.
    private const string ConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public static string GenerateCode()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton(_ => new CosmosClient(ConnectionString));
                    opts.Services.AddScoped<IValidator<CreateThing>, CreateThingValidator>();

                    opts.UseFluentValidation();
                    opts.UseCosmosDbPersistence("wolverine_fsharp_cosmos");
                    opts.Policies.AutoApplyTransactions();

                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Discovery.IncludeType<CreateThingHandler>();
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host (no Roslyn, no Cosmos connection).
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var chain = handlerGraph.ChainFor(typeof(CreateThing))
                        ?? throw new InvalidOperationException("No handler chain was built for CreateThing.");

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
        return Path.Combine(srcTestingDir, "Wolverine.Cosmos.FSharpFixture", "Generated.fs");
    }

    public static string FixtureProjectPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var srcTestingDir = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(srcTestingDir, "Wolverine.Cosmos.FSharpFixture", "Wolverine.Cosmos.FSharpFixture.fsproj");
    }
}
