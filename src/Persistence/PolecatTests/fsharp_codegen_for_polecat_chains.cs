using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Polecat;

namespace PolecatTests;

// GH-5141: the aggregate handler workflow's frames and the session sources moved into Wolverine core, written once
// with the F# rendering only Marten's copies had. A Polecat chain renders as F# now; before, its aggregate load,
// session and session-contract frames had no F# override at all.
public class fsharp_codegen_for_polecat_chains
{
    private static string generateFSharp(Type messageType)
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "fsharp_5141";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FsCounterHandler))
                    .IncludeType(typeof(FsCounterToucher));
            })
            .Build();

        // Compiles the handler graph without starting the host: no database, no Roslyn
        _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

        var handlerGraph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = handlerGraph.ChainFor(messageType)
                    ?? throw new InvalidOperationException($"No handler chain was built for {messageType.Name}");

        var generatedAssembly = handlerGraph.StartAssembly(handlerGraph.Rules);
        ((ICodeFile)chain).AssembleTypes(generatedAssembly);

        return generatedAssembly.GenerateFSharpCode(host.Services.GetService<IServiceVariableSource>());
    }

    [Fact]
    public void the_aggregate_load_and_the_session_render_as_fsharp()
    {
        var code = generateFSharp(typeof(IncrementFsCounter));

        code.ShouldContain("// Building the Polecat session");
        code.ShouldContain("// Loading Polecat aggregate as part of the aggregate handler workflow");
        code.ShouldContain(".Events.FetchForWriting<PolecatTests.FsCounter>(");
        code.ShouldContain("let! ");
    }

    [Fact]
    public void the_session_contracts_render_as_fsharp()
    {
        var code = generateFSharp(typeof(TouchFsCounter));

        code.ShouldContain("// Building the Polecat session");
        code.ShouldContain(":> JasperFx.Events.Documents.IDocumentWriteOperations");
        code.ShouldContain(".Events :> JasperFx.Events.IEventStoreOperations");
    }
}

public class FsCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(FsCounterIncremented _) => Count++;
}

public record FsCounterIncremented;

public record IncrementFsCounter(Guid FsCounterId);

public record TouchFsCounter(Guid Id);

// The [AggregateHandler] shape, as Marten's F# aggregate sample uses. The parameter-level [WriteAggregate] /
// [ReadModel] shape goes through core's LoadEntityFrameBlock, which has no F# rendering on any store yet.
[AggregateHandler]
public static class FsCounterHandler
{
    public static FsCounterIncremented Handle(IncrementFsCounter command, FsCounter counter) => new();
}

public static class FsCounterToucher
{
    public static void Handle(TouchFsCounter command, IDocumentWriteOperations operations,
        JasperFx.Events.IEventStoreOperations events)
    {
    }
}
