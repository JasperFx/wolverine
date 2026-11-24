using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.RegularExpressions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

/// <summary>
/// The aggregate handler workflow resolving a natural key from a <c>TKey?</c> command member, through a
/// mapped key on an async read model, under an exclusive load, and inside a batched query.
/// </summary>
public class natural_key_nullable_mapped_and_batched : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public natural_key_nullable_mapped_and_batched(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_nullable_mapped";
                    m.Projections.Snapshot<NkProduct>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<NkProductStock>(SnapshotLifecycle.Async);
                    m.Projections.Snapshot<NkShelf>(SnapshotLifecycle.Inline);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(NkProductHandler))
                .IncludeType(typeof(NkShelfRecodeHandler));
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        // A natural key identifies one stream, so a rerun against the same database would claim the same
        // keys again. DeleteAllEventDataAsync cascades into the lookup tables.
        await _store.Advanced.Clean.DeleteAllEventDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> listProduct(NkSku sku, int stock = 0)
    {
        var streamId = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkProduct>(streamId, new NkProductListed(sku, "Widget"),
            new NkProductRestocked(stock));
        await session.SaveChangesAsync();
        return streamId;
    }

    private async Task<NkProduct?> loadProduct(Guid streamId)
    {
        await using var query = _store.QuerySession();
        return await query.LoadAsync<NkProduct>(streamId);
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
    public async Task a_null_natural_key_is_refused_on_a_write()
    {
        // As Marten refuses a null string stream id: nothing to load, and no key to claim
        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _host.InvokeMessageAndWaitAsync(new RestockNkProduct(null, 4)));
        ex.Message.ShouldContain("natural key on this message is null");
    }

    [Fact]
    public async Task a_null_reference_type_natural_key_is_refused_on_a_write()
    {
        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _host.InvokeMessageAndWaitAsync(new RecodeNkShelf(null!)));
        ex.Message.ShouldContain("natural key on this message is null");
    }

    [Fact]
    public async Task a_mapped_natural_key_loads_an_async_read_model()
    {
        var sku = new NkSku("MAPPED-1");
        var streamId = await listProduct(sku, 2);

        await _host.InvokeMessageAndWaitAsync(new RestockNkProductStock(sku, 3));

        // The handler appended through the read model's stream, which is the owner's stream too
        (await loadProduct(streamId))!.Stock.ShouldBe(5);
        NkProductHandler.LastStockSeen.ShouldBe(2);
    }

    [Fact]
    public async Task a_natural_key_load_joins_the_batched_query()
    {
        var sku = new NkSku("BATCHED-1");
        var streamId = await listProduct(sku, 1);
        await listProduct(new NkSku("BATCHED-OTHER"), 1);

        var code = sourceFor<AuditNkProduct>();
        code.ShouldContain("CreateBatchQuery");
        code.ShouldContain($"batchedQuery.Events.FetchForWriting<{typeof(NkProduct).FullNameInCode()}, {typeof(NkSku).FullNameInCode()}>");

        await _host.InvokeMessageAndWaitAsync(new AuditNkProduct(sku));

        (await loadProduct(streamId))!.Stock.ShouldBe(2);
        NkProductHandler.LastProductCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    // FetchForWriting<NkProduct, NkSku>(<message>.Sku, <message>.Version...), on the session or the batch
    private static readonly string fetchWithVersion =
        $@"FetchForWriting<{Regex.Escape(typeof(NkProduct).FullNameInCode())}, {Regex.Escape(typeof(NkSku).FullNameInCode())}>\([^;]*\.Sku, [^;]*\.Version[,)]";

    [Fact]
    public async Task a_natural_key_load_checks_the_command_version()
    {
        var sku = new NkSku("VERSION-1");
        var streamId = await listProduct(sku, 1);

        sourceFor<RestockNkProductAtVersion>().ShouldMatch(fetchWithVersion);

        // Listed and restocked: version 2
        await _host.InvokeMessageAndWaitAsync(new RestockNkProductAtVersion(sku, 2, 4));
        (await loadProduct(streamId))!.Stock.ShouldBe(5);

        // Now at version 3, so 2 is stale and nothing is appended
        await Should.ThrowAsync<ConcurrencyException>(() =>
            _host.InvokeMessageAndWaitAsync(new RestockNkProductAtVersion(sku, 2, 10)));
        (await loadProduct(streamId))!.Stock.ShouldBe(5);
    }

    [Fact]
    public async Task a_batched_natural_key_load_checks_the_command_version()
    {
        var sku = new NkSku("VERSION-BATCHED-1");
        var streamId = await listProduct(sku, 1);

        var code = sourceFor<AuditNkProductAtVersion>();
        code.ShouldContain("CreateBatchQuery");
        code.ShouldMatch(fetchWithVersion);

        await _host.InvokeMessageAndWaitAsync(new AuditNkProductAtVersion(sku, 2));
        (await loadProduct(streamId))!.Stock.ShouldBe(2);

        await Should.ThrowAsync<ConcurrencyException>(() =>
            _host.InvokeMessageAndWaitAsync(new AuditNkProductAtVersion(sku, 2)));
        (await loadProduct(streamId))!.Stock.ShouldBe(2);
    }
}

public class natural_key_read_aggregate : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public natural_key_read_aggregate(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_read_aggregate";
                    m.Projections.Snapshot<NkProduct>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<NkProductStock>(SnapshotLifecycle.Async);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(NkProductViewHandler))
                .IncludeType(typeof(NkProductNullableReadHandler));
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await _store.Advanced.Clean.DeleteAllEventDataAsync();
        NkProductViewHandler.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<Guid> listProduct(NkSku sku, int stock)
    {
        var streamId = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkProduct>(streamId, new NkProductListed(sku, "Widget"),
            new NkProductRestocked(stock));
        await session.SaveChangesAsync();
        return streamId;
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
    public async Task reads_an_async_read_model_by_its_mapped_natural_key()
    {
        var sku = new NkSku("READ-MAPPED");
        var streamId = await listProduct(sku, 4);

        await _host.InvokeMessageAndWaitAsync(new ViewNkProductStock(sku));

        // Current state with no daemon running: the owner's lookup, then the events folded in
        NkProductViewHandler.LastStock!.Id.ShouldBe(streamId);
        NkProductViewHandler.LastStock.Stock.ShouldBe(4);
    }

    [Fact]
    public async Task a_null_natural_key_stops_a_required_read()
    {
        // Left in place unless the handler runs, which it must not
        var sentinel = new NkProduct();
        NkProductViewHandler.LastProduct = sentinel;

        await _host.InvokeMessageAndWaitAsync(new ViewNkProductMaybe(null));

        NkProductViewHandler.LastProduct.ShouldBeSameAs(sentinel);
    }

    [Fact]
    public async Task a_null_natural_key_reads_nothing_into_an_optional_read()
    {
        NkProductNullableReadHandler.Called = false;
        await _host.InvokeMessageAndWaitAsync(new PeekNkProduct(null));

        NkProductNullableReadHandler.Called.ShouldBeTrue();
        NkProductNullableReadHandler.LastProduct.ShouldBeNull();
    }

    [Fact]
    public async Task a_null_natural_key_reads_nothing_inside_a_batch()
    {
        await listProduct(new NkSku("PEEK-BATCHED"), 1);

        var code = sourceFor<PeekNkProductWithCatalog>();
        code.ShouldContain("CreateBatchQuery");
        code.ShouldContain("Task.FromResult<");

        NkProductNullableReadHandler.Called = false;
        await _host.InvokeMessageAndWaitAsync(new PeekNkProductWithCatalog(null));

        NkProductNullableReadHandler.Called.ShouldBeTrue();
        NkProductNullableReadHandler.LastProduct.ShouldBeNull();

        // And a key that is present still reads through the batch
        await _host.InvokeMessageAndWaitAsync(new PeekNkProductWithCatalog(new NkSku("PEEK-BATCHED")));
        NkProductNullableReadHandler.LastProduct!.Stock.ShouldBe(1);
    }

    [Fact]
    public async Task a_natural_key_read_joins_the_batched_query()
    {
        var sku = new NkSku("READ-BATCHED");
        await listProduct(sku, 2);

        var code = sourceFor<ViewNkProductWithCatalog>();
        code.ShouldContain(
            $"batchedQuery.Events.FetchLatest<{typeof(NkProduct).FullNameInCode()}, {typeof(NkSku).FullNameInCode()}>");

        await _host.InvokeMessageAndWaitAsync(new ViewNkProductWithCatalog(sku));

        NkProductViewHandler.LastProduct!.Stock.ShouldBe(2);
        NkProductViewHandler.LastCatalogCount.ShouldBeGreaterThanOrEqualTo(1);
    }
}

/// <summary>
/// A command that carries both an owned and a mapped key of the aggregate cannot say which one it means.
/// </summary>
public class natural_key_ambiguous_command : PostgresqlContext
{
    [Fact]
    public async Task a_command_matching_two_natural_keys_is_refused()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_ambiguous";
                    m.Projections.Snapshot<NkProduct>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<NkShelf>(SnapshotLifecycle.Inline);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(NkShelfHandler));
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        var ex = Should.Throw<Exception>(() => host.GetRuntime().Handlers.HandlerFor<StockNkShelf>());

        var message = ex.ToString();
        message.ShouldContain("more than one natural key");
        message.ShouldContain(nameof(StockNkShelf.Sku));
        message.ShouldContain(nameof(StockNkShelf.Code));
    }
}

public readonly record struct NkSku(string Value);

public record NkShelfCode(string Value);

public record NkProductListed(NkSku Sku, string Name);

public record NkProductRestocked(int Quantity);

public record NkShelfPlaced(NkShelfCode Code, NkSku Sku);

public class NkProduct
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkSku Sku { get; set; }

    public string Name { get; set; } = string.Empty;
    public int Stock { get; set; }

    [NaturalKeySource]
    public static NkSku KeyFor(NkProductListed e) => e.Sku;

    public void Apply(NkProductListed e)
    {
        Sku = e.Sku;
        Name = e.Name;
    }

    public void Apply(NkProductRestocked e) => Stock += e.Quantity;
}

#region sample_wolverine_marten_mapped_natural_key
public class NkProductStock
{
    public Guid Id { get; set; }

    [MappedNaturalKey<NkProduct>]
    public NkSku Sku { get; set; }

    public int Stock { get; set; }

    public static NkProductStock Create(NkProductListed e) => new() { Sku = e.Sku };

    public void Apply(NkProductRestocked e) => Stock += e.Quantity;
}

#endregion

// Owns its code and maps the product's key
public class NkShelf
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkShelfCode Code { get; set; } = null!;

    [MappedNaturalKey<NkProduct>]
    public NkSku Sku { get; set; }

    public int Audits { get; set; }

    [NaturalKeySource]
    public static NkShelfCode KeyFor(NkShelfPlaced e) => e.Code;

    public void Apply(NkShelfPlaced e)
    {
        Code = e.Code;
        Sku = e.Sku;
    }

    public void Apply(NkShelfAudited e) => Audits++;
}

public record NkShelfAudited;

#region sample_wolverine_marten_nullable_natural_key_command
// NkSku is a struct, and the command may carry it as NkSku?
public record RestockNkProduct(NkSku? Sku, int Quantity);
#endregion

public record RestockNkProductStock(NkSku Sku, int Quantity);

public record AuditNkProduct(NkSku Sku);

#region sample_wolverine_marten_natural_key_command_with_version
// The version is checked on load, as it is for a command carrying the stream id
public record RestockNkProductAtVersion(NkSku Sku, long Version, int Quantity);
#endregion

public record AuditNkProductAtVersion(NkSku Sku, long Version);

public record StockNkShelf(NkSku Sku, NkShelfCode Code);

public static class NkProductHandler
{
    public static int LastStockSeen;
    public static int LastProductCount;

    public static NkProductRestocked Handle(RestockNkProduct command, [WriteAggregate] NkProduct product)
        => new(command.Quantity);

    #region sample_wolverine_marten_mapped_natural_key_handler
    public static NkProductRestocked Handle(RestockNkProductStock command, [WriteAggregate] NkProductStock stock)
    {
        LastStockSeen = stock.Stock;
        return new NkProductRestocked(command.Quantity);
    }
    #endregion

    public static NkProductRestocked Handle(AuditNkProduct command, [WriteAggregate] NkProduct product,
        [Wolverine.Persistence.All] IReadOnlyList<NkProduct> all)
    {
        LastProductCount = all.Count;
        return new NkProductRestocked(1);
    }

    public static NkProductRestocked Handle(RestockNkProductAtVersion command, [WriteAggregate] NkProduct product)
        => new(command.Quantity);

    public static NkProductRestocked Handle(AuditNkProductAtVersion command, [WriteAggregate] NkProduct product,
        [Wolverine.Persistence.All] IReadOnlyList<NkProduct> all)
    {
        LastProductCount = all.Count;
        return new NkProductRestocked(1);
    }
}

public record ViewNkProduct(NkSku Sku);

public record ViewNkProductMaybe(NkSku? Sku);

public record ViewNkProductStock(NkSku Sku);

public record ViewNkProductWithCatalog(NkSku Sku);

#region sample_wolverine_marten_read_aggregate_by_natural_key
public static class NkProductViewHandler
{
    public static NkProduct? LastProduct;
    public static NkProductStock? LastStock;
    public static int LastCatalogCount;

    public static void Reset()
    {
        LastProduct = null;
        LastStock = null;
        LastCatalogCount = 0;
    }

    // Read-only access by the product's natural key
    public static void Handle(ViewNkProduct query, [ReadAggregate] NkProduct product) => LastProduct = product;

    public static void Handle(ViewNkProductMaybe query, [ReadAggregate] NkProduct product) => LastProduct = product;

    // An async read model, through the product's lookup
    public static void Handle(ViewNkProductStock query, [ReadAggregate] NkProductStock stock) => LastStock = stock;

    public static void Handle(ViewNkProductWithCatalog query, [ReadAggregate] NkProduct product,
        [Wolverine.Persistence.All] IReadOnlyList<NkProduct> catalog)
    {
        LastProduct = product;
        LastCatalogCount = catalog.Count;
    }
}
#endregion

public static class NkShelfHandler
{
    public static NkShelfPlaced Handle(StockNkShelf command, [WriteAggregate] NkShelf shelf)
        => new(command.Code, command.Sku);
}

// A reference-type key, which can be null without being declared TKey?
public record RecodeNkShelf(NkShelfCode Code);

public static class NkShelfRecodeHandler
{
    public static NkShelfPlaced Handle(RecodeNkShelf command, [WriteAggregate] NkShelf shelf)
        => new(command.Code, shelf.Sku);
}

public record PeekNkProduct(NkSku? Sku);

public record PeekNkProductWithCatalog(NkSku? Sku);

public static class NkProductNullableReadHandler
{
    public static bool Called;
    public static NkProduct? LastProduct;

    public static void Handle(PeekNkProduct query, [ReadAggregate(Required = false)] NkProduct? product)
    {
        Called = true;
        LastProduct = product;
    }

    public static void Handle(PeekNkProductWithCatalog query, [ReadAggregate(Required = false)] NkProduct? product,
        [Wolverine.Persistence.All] IReadOnlyList<NkProduct> catalog)
    {
        Called = true;
        LastProduct = product;
    }
}
