using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Fisher;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Fisher;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace FisherTests;

// Two or more batchable reads in one handler share a single Fisher IBatchedQuery. Asserts on the GENERATED
// SOURCE as well as the results, because both forms return identical data -- a results-only test would pass
// just as happily if batching silently never engaged.
public class batched_all_queries : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public batched_all_queries(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("batched_all");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FiInventoryHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await using var session = _host.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new FiPart { Name = "bolt" });
        session.Store(new FiPart { Name = "nut" });
        session.Store(new FiSupplier { Name = "acme" });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
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
        var code = sourceFor<CountFiInventory>();

        code.ShouldContain("CreateBatchQuery()");
        code.ShouldContain("_BatchItem");
        code.Split("CreateBatchQuery()").Length.ShouldBe(2);

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountFiInventory());
        var counted = tracked.Sent.SingleMessage<FiInventoryCounted>();
        counted.Parts.ShouldBe(2);
        counted.Suppliers.ShouldBe(1);
    }

    [Fact]
    public async Task a_lone_all_parameter_is_left_standalone()
    {
        var code = sourceFor<CountFiParts>();

        code.ShouldNotContain("CreateBatchQuery()");
        code.ShouldContain("ToListAsync");

        var tracked = await _host.InvokeMessageAndWaitAsync(new CountFiParts());
        tracked.Sent.SingleMessage<FiPartsCounted>().Parts.ShouldBe(2);
    }
}

public class FiPart
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public class FiSupplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public record CountFiInventory;
public record CountFiParts;
public record FiInventoryCounted(int Parts, int Suppliers);
public record FiPartsCounted(int Parts);

[WolverineIgnore]
public static class FiInventoryHandler
{
    public static FiInventoryCounted Handle(CountFiInventory command,
        [All] IReadOnlyList<FiPart> parts, [All] IReadOnlyList<FiSupplier> suppliers)
        => new(parts.Count, suppliers.Count);

    public static FiPartsCounted Handle(CountFiParts command, [All] IReadOnlyList<FiPart> parts)
        => new(parts.Count);

    public static void Handle(FiInventoryCounted msg) { }
    public static void Handle(FiPartsCounted msg) { }
}
