using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace PolecatTests;

// Two or more batchable reads in one handler share a single Polecat IBatchedQuery. Asserts on the GENERATED
// SOURCE as well as the results, because both forms return identical data -- a results-only test would pass
// just as happily if batching silently never engaged.
public class batched_all_queries : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public batched_all_queries(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PcInventoryHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "pc_batched_all";
                }).IntegrateWithWolverine();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var store = (DocumentStore)_host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
        await store.Advanced.Clean.DeleteAllDocumentsAsync();

        await using var session = store.LightweightSession();
        session.Store(new PcPart { Name = "bolt" });
        session.Store(new PcPart { Name = "nut" });
        session.Store(new PcSupplier { Name = "acme" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var chain = _host.GetRuntime().Handlers.ChainFor<T>();
        chain.ShouldNotBeNull();
        chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(chain.SourceCode);
        return chain.SourceCode;
    }

    [Fact]
    public async Task two_all_parameters_share_one_batched_query()
    {
        var code = sourceFor<CountPcInventory>();

        code.ShouldContain("CreateBatchQuery()");
        code.ShouldContain("_BatchItem");
        code.Split("CreateBatchQuery()").Length.ShouldBe(2);

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountPcInventory());
        var counted = tracked.Sent.SingleMessage<PcInventoryCounted>();
        counted.Parts.ShouldBe(2);
        counted.Suppliers.ShouldBe(1);
    }

    [Fact]
    public async Task a_lone_all_parameter_is_left_standalone()
    {
        var code = sourceFor<CountPcParts>();

        code.ShouldNotContain("CreateBatchQuery()");
        code.ShouldContain("ToListAsync");

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountPcParts());
        tracked.Sent.SingleMessage<PcPartsCounted>().Parts.ShouldBe(2);
    }
}

public class PcPart
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public class PcSupplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public record CountPcInventory;
public record CountPcParts;
public record PcInventoryCounted(int Parts, int Suppliers);
public record PcPartsCounted(int Parts);

[WolverineIgnore]
public static class PcInventoryHandler
{
    public static PcInventoryCounted Handle(CountPcInventory command,
        [All] IReadOnlyList<PcPart> parts, [All] IReadOnlyList<PcSupplier> suppliers)
        => new(parts.Count, suppliers.Count);

    public static PcPartsCounted Handle(CountPcParts command, [All] IReadOnlyList<PcPart> parts)
        => new(parts.Count);

    public static void Handle(PcInventoryCounted msg) { }
    public static void Handle(PcPartsCounted msg) { }
}
