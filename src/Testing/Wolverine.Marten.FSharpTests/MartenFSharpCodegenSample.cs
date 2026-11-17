using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Persistence.Requirements;
using Wolverine.Runtime.Handlers;
using WolverineMartenFSharpSample;

namespace Wolverine.Marten.FSharpTests;

/// <summary>
///     Renders the sample's Marten document handler chain (issue GH-2969) as F#. Builds a minimal host
///     that discovers <see cref="CreateProductHandler" /> with Marten integration + auto-applied
///     transactions, compiles the handler graph without starting it (no DB connection, no Roslyn), and
///     emits the adapter as F# via <see cref="GeneratedAssembly.GenerateFSharpCode" /> — exercising the
///     Marten document frames (open outbox session, SaveChanges). It also renders
///     <see cref="RenameProductHandler" /> with <see cref="DocumentExistsAttribute{TDoc}" /> applied by a
///     handler policy (F# cannot apply a generic attribute), which exercises the existence check's F#
///     emitter.
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
                    opts.Discovery.IncludeType<RenameProductHandler>();

                    opts.Policies.Add(new RequireProductToExist());
                })
                .Build();

            // Force HandlerGraph.Compile() without starting the host (no Roslyn, no DB connection).
            _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);

            foreach (var messageType in new[] { typeof(CreateProductCommand), typeof(RenameProductCommand) })
            {
                var chain = handlerGraph.ChainFor(messageType)
                            ?? throw new InvalidOperationException($"No handler chain was built for {messageType.Name}.");
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
    ///     Puts <c>[DocumentExists&lt;Product&gt;]</c> on the F# RenameProductHandler chain, the way the attribute
    ///     would if F# could apply a generic attribute.
    /// </summary>
    private class RequireProductToExist : IHandlerPolicy
    {
        public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
        {
            foreach (var chain in chains.Where(x => x.MessageType == typeof(RenameProductCommand)))
            {
                new DocumentExistsAttribute<Product>().Modify(chain, rules, container);
            }
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
