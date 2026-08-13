using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
///     Two or more batchable reads in one handler resolve in a single round trip through Marten's
///     <c>IBatchedQuery</c> rather than one query each. A lone read stays standalone.
/// </summary>
/// <remarks>
///     These assert on the GENERATED SOURCE as well as the results, because that is the only way to prove the
///     optimization actually happened -- both the batched and the unbatched forms return identical data, so a
///     results-only test would pass just as happily if batching silently never engaged.
/// </remarks>
public class batched_all_queries : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;

    public batched_all_queries(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InventoryHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "batched_all";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var store = _host.DocumentStore();
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Part));
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Supplier));

        await store.BulkInsertDocumentsAsync(
            [new Part { Name = "bolt" }, new Part { Name = "nut" }],
            cancellation: TestContext.Current.CancellationToken);
        await store.BulkInsertDocumentsAsync(
            [new Supplier { Name = "acme" }],
            cancellation: TestContext.Current.CancellationToken);
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
        var code = sourceFor<CountInventory>();

        code.ShouldContain("CreateBatchQuery()");
        code.ShouldContain("_BatchItem");
        code.ShouldContain(".Execute(");

        // ...and one batch, not two
        code.Split("CreateBatchQuery()").Length.ShouldBe(2);

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountInventory());
        var counted = tracked.Sent.SingleMessage<InventoryCounted>();
        counted.Parts.ShouldBe(2);
        counted.Suppliers.ShouldBe(1);
    }

    [Fact]
    public async Task a_lone_all_parameter_is_left_standalone()
    {
        // The batch-of-one is deliberately NOT taken: a single read gains nothing from the batch machinery,
        // and Marten's batching policy short circuits on frames.Count <= 1.
        var code = sourceFor<CountParts>();

        code.ShouldNotContain("CreateBatchQuery()");
        code.ShouldContain("ToListAsync");

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountParts());
        tracked.Sent.SingleMessage<PartsCounted>().Parts.ShouldBe(2);
    }
}

public class Part
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public record CountInventory;
public record CountParts;
public record InventoryCounted(int Parts, int Suppliers);
public record PartsCounted(int Parts);

[WolverineIgnore]
public static class InventoryHandler
{
    public static InventoryCounted Handle(CountInventory command,
        [All] IReadOnlyList<Part> parts, [All] IReadOnlyList<Supplier> suppliers)
        => new(parts.Count, suppliers.Count);

    public static PartsCounted Handle(CountParts command, [All] IReadOnlyList<Part> parts)
        => new(parts.Count);

    public static void Handle(InventoryCounted msg) { }
    public static void Handle(PartsCounted msg) { }
}
