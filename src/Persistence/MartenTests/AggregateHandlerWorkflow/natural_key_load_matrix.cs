using System.Reflection;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

#region Load matrix aggregates

public readonly record struct MxSku(string Value);

public readonly record struct MxBin(int Value);

public record MxCode(string Value);

public record MxSkuPlaced(MxSku Key);

public record MxBinPlaced(MxBin Key);

public record MxCodePlaced(MxCode Key);

public record MxRenamed(string Name);

public class MxSkuOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MxSku Key { get; set; }

    public string Name { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(MxSkuPlaced e) => Key = e.Key;

    public void Apply(MxRenamed e) => Name = e.Name;
}

public class MxBinOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MxBin Key { get; set; }

    public string Name { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(MxBinPlaced e) => Key = e.Key;

    public void Apply(MxRenamed e) => Name = e.Name;
}

public class MxCodeOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public MxCode Key { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(MxCodePlaced e) => Key = e.Key;

    public void Apply(MxRenamed e) => Name = e.Name;
}

/// <summary>An Async view of the <see cref="MxSkuOrder" /> streams, found through that aggregate's lookup.</summary>
public class MxSkuView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<MxSkuOrder>]
    public MxSku Key { get; set; }

    public string Name { get; set; } = string.Empty;

    public void Apply(MxSkuPlaced e) => Key = e.Key;

    public void Apply(MxRenamed e) => Name = e.Name;
}

#endregion

/// <summary>
/// Every attribute that loads an aggregate, for every shape of natural key: a struct wrapping a string, a
/// struct wrapping an int, a class key, a <c>TKey?</c> member, and a mapped key of an Async view. Then each
/// write attribute batched with another load, at a version, and exclusively.
/// <see cref="natural_key_load_matrix_with_conjoined_tenancy" /> runs it all again in one tenant of a conjoined
/// store, with a stream for the same key in another tenant.
/// </summary>
public class natural_key_load_matrix : PostgresqlContext, IAsyncLifetime
{
    private const string OtherTenant = "tenant-b";

    private readonly ITestOutputHelper _output;
    private readonly string? _tenant;

    // The other tenant's stream for the same key, by the stream a test placed
    private readonly Dictionary<Guid, Guid> _twins = new();

    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public natural_key_load_matrix(ITestOutputHelper output) : this(output, null)
    {
    }

    protected natural_key_load_matrix(ITestOutputHelper output, string? tenant)
    {
        _output = output;
        _tenant = tenant;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = _tenant == null ? "nk_load_matrix" : "nk_load_matrix_conjoined";
                    if (_tenant != null)
                    {
                        m.Policies.AllDocumentsAreMultiTenanted();
                        m.Events.TenancyStyle = TenancyStyle.Conjoined;
                    }

                    m.Projections.Snapshot<MxSkuOrder>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<MxBinOrder>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<MxCodeOrder>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<MxSkuView>(SnapshotLifecycle.Async);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            // Only compiled when a test asks for one, so the refused decider shape doesn't stop the host
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(MxLoadMatrixHandlers))
                .IncludeType(typeof(MxLoadContextHandlers))
                .IncludeType(typeof(MxDeciderHandlers));
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        MxLoadMatrixHandlers.LastRead = null;
        MxLoadContextHandlers.LastBin = null;
        MxLoadContextHandlers.LastView = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // The lookup tables outlive the run and a key names one stream, so every key is new
    private static object keyFor(Type owner) => owner == typeof(MxBinOrder)
        ? new MxBin(Random.Shared.Next(1, int.MaxValue))
        : owner == typeof(MxCodeOrder)
            ? new MxCode("C-" + Guid.NewGuid().ToString("N"))
            : new MxSku("S-" + Guid.NewGuid().ToString("N"));

    private static object placedFor(object key) => key switch
    {
        MxSku sku => new MxSkuPlaced(sku),
        MxBin bin => new MxBinPlaced(bin),
        MxCode code => new MxCodePlaced(code),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private IDocumentSession sessionFor(string? tenant) =>
        tenant == null ? _store.LightweightSession() : _store.LightweightSession(tenant);

    private async Task<Guid> placeAsync(Type owner, object key)
    {
        var streamId = Guid.NewGuid();
        await using (var session = sessionFor(_tenant))
        {
            session.Events.StartStream(owner, streamId, placedFor(key));
            await session.SaveChangesAsync();
        }

        if (_tenant != null)
        {
            var twin = Guid.NewGuid();
            await using var other = sessionFor(OtherTenant);
            other.Events.StartStream(owner, twin, placedFor(key));
            await other.SaveChangesAsync();
            _twins[streamId] = twin;
        }

        return streamId;
    }

    // The other tenant's stream for the same key still has only the event that placed it
    private async Task twinIsUntouchedAsync(Guid streamId)
    {
        if (_tenant == null) return;

        await using var query = _store.QuerySession(OtherTenant);
        (await query.Events.FetchStreamAsync(_twins[streamId])).Count.ShouldBe(1);
    }

    private Task invokeAsync(object message) => _tenant == null
        ? _host.InvokeMessageAndWaitAsync(message)
        : _host.InvokeMessageAndWaitAsync(message, _tenant);

    private async Task<IReadOnlyList<IEvent>> eventsOf(Guid streamId)
    {
        await using var query = _tenant == null ? _store.QuerySession() : _store.QuerySession(_tenant);
        return await query.Events.FetchStreamAsync(streamId);
    }

    private string sourceFor(object message)
    {
        var handlers = _host.GetRuntime().Handlers;
        handlers.HandlerFor(message.GetType());
        var code = handlers.ChainFor(message.GetType())!.SourceCode!;
        _output.WriteLine(code);
        return code;
    }

    public static IEnumerable<object[]> WriteCases() =>
        MxLoadMatrixCases.Writes.Select(x => new object[] { x.Attribute, x.Shape });

    public static IEnumerable<object[]> ReadCases() =>
        MxLoadMatrixCases.Reads.Select(x => new object[] { x.Attribute, x.Shape });

    [Theory]
    [MemberData(nameof(WriteCases))]
    public async Task a_write_loads_by_the_natural_key(string attribute, string shape)
    {
        var @case = MxLoadMatrixCases.Writes.Single(x => x.Attribute == attribute && x.Shape == shape);
        var key = keyFor(@case.Owner);
        var streamId = await placeAsync(@case.Owner, key);
        var message = @case.Message(key);

        sourceFor(message).ShouldContain(
            $"FetchForWriting<{@case.Aggregate.FullNameInCode()}, {key.GetType().FullNameInCode()}>");

        await invokeAsync(message);

        var events = await eventsOf(streamId);
        events.Count.ShouldBe(2);
        events[1].Data.ShouldBe(new MxRenamed("Renamed"));
        await twinIsUntouchedAsync(streamId);
    }

    [Theory]
    [MemberData(nameof(ReadCases))]
    public async Task a_read_loads_by_the_natural_key(string attribute, string shape)
    {
        var @case = MxLoadMatrixCases.Reads.Single(x => x.Attribute == attribute && x.Shape == shape);
        var key = keyFor(@case.Owner);
        var streamId = await placeAsync(@case.Owner, key);
        var message = @case.Message(key);

        sourceFor(message).ShouldContain(
            $"FetchLatest<{@case.Aggregate.FullNameInCode()}, {key.GetType().FullNameInCode()}>");

        await invokeAsync(message);

        MxLoadMatrixHandlers.LastRead.ShouldNotBeNull();
        MxLoadMatrixHandlers.LastRead.GetType().ShouldBe(@case.Aggregate);
        @case.Aggregate.GetProperty("Id")!.GetValue(MxLoadMatrixHandlers.LastRead).ShouldBe(streamId);
    }

    [Theory]
    [MemberData(nameof(ReadCases))]
    public async Task an_unknown_key_stops_a_required_read(string attribute, string shape)
    {
        var @case = MxLoadMatrixCases.Reads.Single(x => x.Attribute == attribute && x.Shape == shape);

        await invokeAsync(@case.Message(keyFor(@case.Owner)));

        MxLoadMatrixHandlers.LastRead.ShouldBeNull();
    }

    // ---------- each write attribute batched, at a version, and exclusive ----------

    public static IEnumerable<object[]> ContextAttributes() =>
        new[] { "WriteAggregate", "WriteAggregateOfKey", "WriteModel", "WriteModelOfKey", "ConsistentAggregate" }
            .Select(x => new object[] { x });

    [Theory]
    [MemberData(nameof(ContextAttributes))]
    public async Task a_write_by_key_joins_the_batched_query(string attribute)
    {
        var sku = (MxSku)keyFor(typeof(MxSkuOrder));
        var bin = (MxBin)keyFor(typeof(MxBinOrder));
        var orderId = await placeAsync(typeof(MxSkuOrder), sku);
        var binId = await placeAsync(typeof(MxBinOrder), bin);

        var message = MxLoadContextHandlers.Batched(attribute, sku, bin);
        var code = sourceFor(message);
        code.ShouldContain(
            $"batchedQuery.Events.FetchForWriting<{typeof(MxSkuOrder).FullNameInCode()}, {typeof(MxSku).FullNameInCode()}>");
        code.ShouldContain(
            $"batchedQuery.Events.FetchLatest<{typeof(MxBinOrder).FullNameInCode()}, {typeof(MxBin).FullNameInCode()}>");

        await invokeAsync(message);

        (await eventsOf(orderId))[1].Data.ShouldBe(new MxRenamed("Batched"));
        MxLoadContextHandlers.LastBin!.Id.ShouldBe(binId);
    }

    [Theory]
    [MemberData(nameof(ContextAttributes))]
    public async Task a_write_by_key_checks_the_version(string attribute)
    {
        var sku = (MxSku)keyFor(typeof(MxSkuOrder));
        var orderId = await placeAsync(typeof(MxSkuOrder), sku);

        await invokeAsync(MxLoadContextHandlers.Versioned(attribute, sku, 1));
        (await eventsOf(orderId)).Count.ShouldBe(2);

        // Now at version 2, so 1 is stale and nothing is appended
        await Should.ThrowAsync<ConcurrencyException>(() =>
            invokeAsync(MxLoadContextHandlers.Versioned(attribute, sku, 1)));
        (await eventsOf(orderId)).Count.ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(ContextAttributes))]
    public async Task a_batched_write_by_key_checks_the_version(string attribute)
    {
        var sku = (MxSku)keyFor(typeof(MxSkuOrder));
        var bin = (MxBin)keyFor(typeof(MxBinOrder));
        var orderId = await placeAsync(typeof(MxSkuOrder), sku);
        await placeAsync(typeof(MxBinOrder), bin);

        var message = MxLoadContextHandlers.BatchedVersioned(attribute, sku, bin, 1);
        sourceFor(message).ShouldContain(
            $"batchedQuery.Events.FetchForWriting<{typeof(MxSkuOrder).FullNameInCode()}, {typeof(MxSku).FullNameInCode()}>");

        await invokeAsync(message);
        (await eventsOf(orderId)).Count.ShouldBe(2);

        // Now at version 2, so 1 is stale and nothing is appended
        await Should.ThrowAsync<ConcurrencyException>(() =>
            invokeAsync(MxLoadContextHandlers.BatchedVersioned(attribute, sku, bin, 1)));
        (await eventsOf(orderId)).Count.ShouldBe(2);
        await twinIsUntouchedAsync(orderId);
    }

    [Theory]
    [MemberData(nameof(ContextAttributes))]
    public async Task an_exclusive_write_by_key_locks_the_stream(string attribute)
    {
        var sku = (MxSku)keyFor(typeof(MxSkuOrder));
        var orderId = await placeAsync(typeof(MxSkuOrder), sku);

        var message = MxLoadContextHandlers.Exclusive(attribute, sku);
        sourceFor(message).ShouldContain(
            $"FetchForExclusiveWriting<{typeof(MxSkuOrder).FullNameInCode()}, {typeof(MxSku).FullNameInCode()}>");

        await invokeAsync(message);
        (await eventsOf(orderId))[1].Data.ShouldBe(new MxRenamed("Exclusive"));
    }

    [Fact]
    public async Task an_async_aggregate_by_key_joins_a_batch_beside_an_exclusive_load()
    {
        var bin = (MxBin)keyFor(typeof(MxBinOrder));
        var sku = (MxSku)keyFor(typeof(MxSkuOrder));
        var binId = await placeAsync(typeof(MxBinOrder), bin);
        var orderId = await placeAsync(typeof(MxSkuOrder), sku);

        var message = new MxExclusiveBesideAsyncView(bin, sku, "Beside");
        var code = sourceFor(message);
        code.ShouldContain(
            $"batchedQuery.Events.FetchForExclusiveWriting<{typeof(MxBinOrder).FullNameInCode()}, {typeof(MxBin).FullNameInCode()}>");
        code.ShouldContain(
            $"batchedQuery.Events.FetchLatest<{typeof(MxSkuView).FullNameInCode()}, {typeof(MxSku).FullNameInCode()}>");

        await invokeAsync(message);

        (await eventsOf(binId))[1].Data.ShouldBe(new MxRenamed("Beside"));

        // No daemon has run, so the view is the owner's stream folded through its lookup
        MxLoadContextHandlers.LastView!.Id.ShouldBe(orderId);
        MxLoadContextHandlers.LastView.Key.ShouldBe(sku);
    }

    // ---------- the decider-function form ----------

    // GH-5948: [AggregateHandler] loads by a natural key alone, in one tenant of a conjoined store too
    [Fact]
    public async Task aggregate_handler_loads_by_natural_key()
    {
        var key = keyFor(typeof(MxSkuOrder));
        var streamId = await placeAsync(typeof(MxSkuOrder), key);
        var message = new MxDeciderBySku((MxSku)key, "Renamed");

        sourceFor(message).ShouldContain(
            $"FetchForWriting<{typeof(MxSkuOrder).FullNameInCode()}, {typeof(MxSku).FullNameInCode()}>");

        await invokeAsync(message);

        var events = await eventsOf(streamId);
        events.Count.ShouldBe(2);
        events[1].Data.ShouldBe(new MxRenamed("Renamed"));
        await twinIsUntouchedAsync(streamId);
    }
}

public class natural_key_load_matrix_with_conjoined_tenancy(ITestOutputHelper output)
    : natural_key_load_matrix(output, "tenant-a");

// One message and handler per attribute and key shape.


public record MxWriteAggregateSku(MxSku Key, string Name);
public record MxWriteAggregateBin(MxBin Key, string Name);
public record MxWriteAggregateCode(MxCode Key, string Name);
public record MxWriteAggregateNullableSku(MxSku? Key, string Name);
public record MxWriteAggregateMappedSku(MxSku Key, string Name);
public record MxWriteAggregateOfKeySku(MxSku Key, string Name);
public record MxWriteAggregateOfKeyBin(MxBin Key, string Name);
public record MxWriteAggregateOfKeyCode(MxCode Key, string Name);
public record MxWriteAggregateOfKeyNullableSku(MxSku? Key, string Name);
public record MxWriteAggregateOfKeyMappedSku(MxSku Key, string Name);
public record MxWriteModelSku(MxSku Key, string Name);
public record MxWriteModelBin(MxBin Key, string Name);
public record MxWriteModelCode(MxCode Key, string Name);
public record MxWriteModelNullableSku(MxSku? Key, string Name);
public record MxWriteModelMappedSku(MxSku Key, string Name);
public record MxWriteModelOfKeySku(MxSku Key, string Name);
public record MxWriteModelOfKeyBin(MxBin Key, string Name);
public record MxWriteModelOfKeyCode(MxCode Key, string Name);
public record MxWriteModelOfKeyNullableSku(MxSku? Key, string Name);
public record MxWriteModelOfKeyMappedSku(MxSku Key, string Name);
public record MxWriteAggregateNaturalKeySku(MxSku Key, string Name);
public record MxWriteAggregateNaturalKeyBin(MxBin Key, string Name);
public record MxWriteAggregateNaturalKeyCode(MxCode Key, string Name);
public record MxWriteAggregateNaturalKeyNullableSku(MxSku? Key, string Name);
public record MxWriteAggregateNaturalKeyMappedSku(MxSku Key, string Name);
public record MxConsistentAggregateSku(MxSku Key, string Name);
public record MxConsistentAggregateBin(MxBin Key, string Name);
public record MxConsistentAggregateCode(MxCode Key, string Name);
public record MxConsistentAggregateNullableSku(MxSku? Key, string Name);
public record MxConsistentAggregateMappedSku(MxSku Key, string Name);
public record MxReadAggregateSku(MxSku Key);
public record MxReadAggregateBin(MxBin Key);
public record MxReadAggregateCode(MxCode Key);
public record MxReadAggregateNullableSku(MxSku? Key);
public record MxReadAggregateMappedSku(MxSku Key);
public record MxReadAggregateOfKeySku(MxSku Key);
public record MxReadAggregateOfKeyBin(MxBin Key);
public record MxReadAggregateOfKeyCode(MxCode Key);
public record MxReadAggregateOfKeyNullableSku(MxSku? Key);
public record MxReadAggregateOfKeyMappedSku(MxSku Key);
public record MxReadModelSku(MxSku Key);
public record MxReadModelBin(MxBin Key);
public record MxReadModelCode(MxCode Key);
public record MxReadModelNullableSku(MxSku? Key);
public record MxReadModelMappedSku(MxSku Key);
public record MxReadModelOfKeySku(MxSku Key);
public record MxReadModelOfKeyBin(MxBin Key);
public record MxReadModelOfKeyCode(MxCode Key);
public record MxReadModelOfKeyNullableSku(MxSku? Key);
public record MxReadModelOfKeyMappedSku(MxSku Key);
public record MxReadAggregateNaturalKeySku(MxSku Key);
public record MxReadAggregateNaturalKeyBin(MxBin Key);
public record MxReadAggregateNaturalKeyCode(MxCode Key);
public record MxReadAggregateNaturalKeyNullableSku(MxSku? Key);
public record MxReadAggregateNaturalKeyMappedSku(MxSku Key);

public static class MxLoadMatrixHandlers
{
    public static object? LastRead;

    public static MxRenamed Handle(MxWriteAggregateSku c, [WriteAggregate] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateBin c, [WriteAggregate] MxBinOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateCode c, [WriteAggregate] MxCodeOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateNullableSku c, [WriteAggregate] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateMappedSku c, [WriteAggregate] MxSkuView aggregate) => new(c.Name);

    public static MxRenamed Handle(MxWriteAggregateOfKeySku c, [WriteAggregate<MxSku>] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateOfKeyBin c, [WriteAggregate<MxBin>] MxBinOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateOfKeyCode c, [WriteAggregate<MxCode>] MxCodeOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateOfKeyNullableSku c, [WriteAggregate<MxSku>] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateOfKeyMappedSku c, [WriteAggregate<MxSku>] MxSkuView aggregate) => new(c.Name);

    public static MxRenamed Handle(MxWriteModelSku c, [WriteModel] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelBin c, [WriteModel] MxBinOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelCode c, [WriteModel] MxCodeOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelNullableSku c, [WriteModel] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelMappedSku c, [WriteModel] MxSkuView aggregate) => new(c.Name);

    public static MxRenamed Handle(MxWriteModelOfKeySku c, [WriteModel<MxSku>] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelOfKeyBin c, [WriteModel<MxBin>] MxBinOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelOfKeyCode c, [WriteModel<MxCode>] MxCodeOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelOfKeyNullableSku c, [WriteModel<MxSku>] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteModelOfKeyMappedSku c, [WriteModel<MxSku>] MxSkuView aggregate) => new(c.Name);

    public static MxRenamed Handle(MxWriteAggregateNaturalKeySku c, [WriteAggregate(NaturalKey = typeof(MxSku))] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateNaturalKeyBin c, [WriteAggregate(NaturalKey = typeof(MxBin))] MxBinOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateNaturalKeyCode c, [WriteAggregate(NaturalKey = typeof(MxCode))] MxCodeOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateNaturalKeyNullableSku c, [WriteAggregate(NaturalKey = typeof(MxSku))] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxWriteAggregateNaturalKeyMappedSku c, [WriteAggregate(NaturalKey = typeof(MxSku))] MxSkuView aggregate) => new(c.Name);

    public static MxRenamed Handle(MxConsistentAggregateSku c, [ConsistentAggregate] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxConsistentAggregateBin c, [ConsistentAggregate] MxBinOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxConsistentAggregateCode c, [ConsistentAggregate] MxCodeOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxConsistentAggregateNullableSku c, [ConsistentAggregate] MxSkuOrder aggregate) => new(c.Name);
    public static MxRenamed Handle(MxConsistentAggregateMappedSku c, [ConsistentAggregate] MxSkuView aggregate) => new(c.Name);

    public static void Handle(MxReadAggregateSku q, [ReadAggregate] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateBin q, [ReadAggregate] MxBinOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateCode q, [ReadAggregate] MxCodeOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateNullableSku q, [ReadAggregate] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateMappedSku q, [ReadAggregate] MxSkuView aggregate) => LastRead = aggregate;

    public static void Handle(MxReadAggregateOfKeySku q, [ReadAggregate<MxSku>] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateOfKeyBin q, [ReadAggregate<MxBin>] MxBinOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateOfKeyCode q, [ReadAggregate<MxCode>] MxCodeOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateOfKeyNullableSku q, [ReadAggregate<MxSku>] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateOfKeyMappedSku q, [ReadAggregate<MxSku>] MxSkuView aggregate) => LastRead = aggregate;

    public static void Handle(MxReadModelSku q, [ReadModel] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelBin q, [ReadModel] MxBinOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelCode q, [ReadModel] MxCodeOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelNullableSku q, [ReadModel] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelMappedSku q, [ReadModel] MxSkuView aggregate) => LastRead = aggregate;

    public static void Handle(MxReadModelOfKeySku q, [ReadModel<MxSku>] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelOfKeyBin q, [ReadModel<MxBin>] MxBinOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelOfKeyCode q, [ReadModel<MxCode>] MxCodeOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelOfKeyNullableSku q, [ReadModel<MxSku>] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadModelOfKeyMappedSku q, [ReadModel<MxSku>] MxSkuView aggregate) => LastRead = aggregate;

    public static void Handle(MxReadAggregateNaturalKeySku q, [ReadAggregate(NaturalKey = typeof(MxSku))] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateNaturalKeyBin q, [ReadAggregate(NaturalKey = typeof(MxBin))] MxBinOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateNaturalKeyCode q, [ReadAggregate(NaturalKey = typeof(MxCode))] MxCodeOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateNaturalKeyNullableSku q, [ReadAggregate(NaturalKey = typeof(MxSku))] MxSkuOrder aggregate) => LastRead = aggregate;
    public static void Handle(MxReadAggregateNaturalKeyMappedSku q, [ReadAggregate(NaturalKey = typeof(MxSku))] MxSkuView aggregate) => LastRead = aggregate;

}

internal static class MxLoadMatrixCases
{
    public record Case(string Attribute, string Shape, Type Aggregate, Type Owner, Func<object, object> Message);

    public static readonly Case[] Writes =
    [
        new("WriteAggregate", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteAggregateSku((MxSku)k, "Renamed")),
        new("WriteAggregate", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxWriteAggregateBin((MxBin)k, "Renamed")),
        new("WriteAggregate", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxWriteAggregateCode((MxCode)k, "Renamed")),
        new("WriteAggregate", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteAggregateNullableSku((MxSku)k, "Renamed")),
        new("WriteAggregate", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxWriteAggregateMappedSku((MxSku)k, "Renamed")),
        new("WriteAggregateOfKey", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteAggregateOfKeySku((MxSku)k, "Renamed")),
        new("WriteAggregateOfKey", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxWriteAggregateOfKeyBin((MxBin)k, "Renamed")),
        new("WriteAggregateOfKey", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxWriteAggregateOfKeyCode((MxCode)k, "Renamed")),
        new("WriteAggregateOfKey", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteAggregateOfKeyNullableSku((MxSku)k, "Renamed")),
        new("WriteAggregateOfKey", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxWriteAggregateOfKeyMappedSku((MxSku)k, "Renamed")),
        new("WriteModel", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteModelSku((MxSku)k, "Renamed")),
        new("WriteModel", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxWriteModelBin((MxBin)k, "Renamed")),
        new("WriteModel", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxWriteModelCode((MxCode)k, "Renamed")),
        new("WriteModel", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteModelNullableSku((MxSku)k, "Renamed")),
        new("WriteModel", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxWriteModelMappedSku((MxSku)k, "Renamed")),
        new("WriteModelOfKey", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteModelOfKeySku((MxSku)k, "Renamed")),
        new("WriteModelOfKey", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxWriteModelOfKeyBin((MxBin)k, "Renamed")),
        new("WriteModelOfKey", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxWriteModelOfKeyCode((MxCode)k, "Renamed")),
        new("WriteModelOfKey", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteModelOfKeyNullableSku((MxSku)k, "Renamed")),
        new("WriteModelOfKey", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxWriteModelOfKeyMappedSku((MxSku)k, "Renamed")),
        new("WriteAggregateNaturalKey", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteAggregateNaturalKeySku((MxSku)k, "Renamed")),
        new("WriteAggregateNaturalKey", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxWriteAggregateNaturalKeyBin((MxBin)k, "Renamed")),
        new("WriteAggregateNaturalKey", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxWriteAggregateNaturalKeyCode((MxCode)k, "Renamed")),
        new("WriteAggregateNaturalKey", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxWriteAggregateNaturalKeyNullableSku((MxSku)k, "Renamed")),
        new("WriteAggregateNaturalKey", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxWriteAggregateNaturalKeyMappedSku((MxSku)k, "Renamed")),
        new("ConsistentAggregate", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxConsistentAggregateSku((MxSku)k, "Renamed")),
        new("ConsistentAggregate", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxConsistentAggregateBin((MxBin)k, "Renamed")),
        new("ConsistentAggregate", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxConsistentAggregateCode((MxCode)k, "Renamed")),
        new("ConsistentAggregate", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxConsistentAggregateNullableSku((MxSku)k, "Renamed")),
        new("ConsistentAggregate", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxConsistentAggregateMappedSku((MxSku)k, "Renamed")),
    ];

    public static readonly Case[] Reads =
    [
        new("ReadAggregate", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadAggregateSku((MxSku)k)),
        new("ReadAggregate", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxReadAggregateBin((MxBin)k)),
        new("ReadAggregate", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxReadAggregateCode((MxCode)k)),
        new("ReadAggregate", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadAggregateNullableSku((MxSku)k)),
        new("ReadAggregate", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxReadAggregateMappedSku((MxSku)k)),
        new("ReadAggregateOfKey", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadAggregateOfKeySku((MxSku)k)),
        new("ReadAggregateOfKey", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxReadAggregateOfKeyBin((MxBin)k)),
        new("ReadAggregateOfKey", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxReadAggregateOfKeyCode((MxCode)k)),
        new("ReadAggregateOfKey", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadAggregateOfKeyNullableSku((MxSku)k)),
        new("ReadAggregateOfKey", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxReadAggregateOfKeyMappedSku((MxSku)k)),
        new("ReadModel", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadModelSku((MxSku)k)),
        new("ReadModel", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxReadModelBin((MxBin)k)),
        new("ReadModel", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxReadModelCode((MxCode)k)),
        new("ReadModel", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadModelNullableSku((MxSku)k)),
        new("ReadModel", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxReadModelMappedSku((MxSku)k)),
        new("ReadModelOfKey", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadModelOfKeySku((MxSku)k)),
        new("ReadModelOfKey", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxReadModelOfKeyBin((MxBin)k)),
        new("ReadModelOfKey", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxReadModelOfKeyCode((MxCode)k)),
        new("ReadModelOfKey", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadModelOfKeyNullableSku((MxSku)k)),
        new("ReadModelOfKey", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxReadModelOfKeyMappedSku((MxSku)k)),
        new("ReadAggregateNaturalKey", "Sku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadAggregateNaturalKeySku((MxSku)k)),
        new("ReadAggregateNaturalKey", "Bin", typeof(MxBinOrder), typeof(MxBinOrder), k => new MxReadAggregateNaturalKeyBin((MxBin)k)),
        new("ReadAggregateNaturalKey", "Code", typeof(MxCodeOrder), typeof(MxCodeOrder), k => new MxReadAggregateNaturalKeyCode((MxCode)k)),
        new("ReadAggregateNaturalKey", "NullableSku", typeof(MxSkuOrder), typeof(MxSkuOrder), k => new MxReadAggregateNaturalKeyNullableSku((MxSku)k)),
        new("ReadAggregateNaturalKey", "MappedSku", typeof(MxSkuView), typeof(MxSkuOrder), k => new MxReadAggregateNaturalKeyMappedSku((MxSku)k)),
    ];
}

public record MxBatchedWriteAggregate(MxSku Key, MxBin Bin, string Name);
public record MxBatchedWriteAggregateOfKey(MxSku Key, MxBin Bin, string Name);
public record MxBatchedWriteModel(MxSku Key, MxBin Bin, string Name);
public record MxBatchedWriteModelOfKey(MxSku Key, MxBin Bin, string Name);
public record MxBatchedConsistentAggregate(MxSku Key, MxBin Bin, string Name);

public record MxVersionedWriteAggregate(MxSku Key, long Version, string Name);
public record MxVersionedWriteAggregateOfKey(MxSku Key, long Version, string Name);
public record MxVersionedWriteModel(MxSku Key, long Version, string Name);
public record MxVersionedWriteModelOfKey(MxSku Key, long Version, string Name);
public record MxVersionedConsistentAggregate(MxSku Key, long Version, string Name);

public record MxBatchedVersionedWriteAggregate(MxSku Key, MxBin Bin, long Version, string Name);
public record MxBatchedVersionedWriteAggregateOfKey(MxSku Key, MxBin Bin, long Version, string Name);
public record MxBatchedVersionedWriteModel(MxSku Key, MxBin Bin, long Version, string Name);
public record MxBatchedVersionedWriteModelOfKey(MxSku Key, MxBin Bin, long Version, string Name);
public record MxBatchedVersionedConsistentAggregate(MxSku Key, MxBin Bin, long Version, string Name);

public record MxExclusiveWriteAggregate(MxSku Key, string Name);
public record MxExclusiveWriteAggregateOfKey(MxSku Key, string Name);
public record MxExclusiveWriteModel(MxSku Key, string Name);
public record MxExclusiveWriteModelOfKey(MxSku Key, string Name);
public record MxExclusiveConsistentAggregate(MxSku Key, string Name);

public record MxExclusiveBesideAsyncView(MxBin Bin, MxSku Sku, string Name);

public static class MxLoadContextHandlers
{
    public static MxBinOrder? LastBin;
    public static MxSkuView? LastView;

    public static MxRenamed Handle(MxExclusiveBesideAsyncView c,
        [WriteAggregate<MxBin>(nameof(MxExclusiveBesideAsyncView.Bin), LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)]
        MxBinOrder bin,
        [ReadAggregate<MxSku>(nameof(MxExclusiveBesideAsyncView.Sku))] MxSkuView view)
    {
        LastView = view;
        return new MxRenamed(c.Name);
    }

    public static object Batched(string attribute, MxSku key, MxBin bin) => attribute switch
    {
        "WriteAggregate" => new MxBatchedWriteAggregate(key, bin, "Batched"),
        "WriteAggregateOfKey" => new MxBatchedWriteAggregateOfKey(key, bin, "Batched"),
        "WriteModel" => new MxBatchedWriteModel(key, bin, "Batched"),
        "WriteModelOfKey" => new MxBatchedWriteModelOfKey(key, bin, "Batched"),
        "ConsistentAggregate" => new MxBatchedConsistentAggregate(key, bin, "Batched"),
        _ => throw new ArgumentOutOfRangeException(nameof(attribute))
    };

    public static object BatchedVersioned(string attribute, MxSku key, MxBin bin, long version) => attribute switch
    {
        "WriteAggregate" => new MxBatchedVersionedWriteAggregate(key, bin, version, "BatchedVersioned"),
        "WriteAggregateOfKey" => new MxBatchedVersionedWriteAggregateOfKey(key, bin, version, "BatchedVersioned"),
        "WriteModel" => new MxBatchedVersionedWriteModel(key, bin, version, "BatchedVersioned"),
        "WriteModelOfKey" => new MxBatchedVersionedWriteModelOfKey(key, bin, version, "BatchedVersioned"),
        "ConsistentAggregate" => new MxBatchedVersionedConsistentAggregate(key, bin, version, "BatchedVersioned"),
        _ => throw new ArgumentOutOfRangeException(nameof(attribute))
    };

    public static object Versioned(string attribute, MxSku key, long version) => attribute switch
    {
        "WriteAggregate" => new MxVersionedWriteAggregate(key, version, "Versioned"),
        "WriteAggregateOfKey" => new MxVersionedWriteAggregateOfKey(key, version, "Versioned"),
        "WriteModel" => new MxVersionedWriteModel(key, version, "Versioned"),
        "WriteModelOfKey" => new MxVersionedWriteModelOfKey(key, version, "Versioned"),
        "ConsistentAggregate" => new MxVersionedConsistentAggregate(key, version, "Versioned"),
        _ => throw new ArgumentOutOfRangeException(nameof(attribute))
    };

    public static object Exclusive(string attribute, MxSku key) => attribute switch
    {
        "WriteAggregate" => new MxExclusiveWriteAggregate(key, "Exclusive"),
        "WriteAggregateOfKey" => new MxExclusiveWriteAggregateOfKey(key, "Exclusive"),
        "WriteModel" => new MxExclusiveWriteModel(key, "Exclusive"),
        "WriteModelOfKey" => new MxExclusiveWriteModelOfKey(key, "Exclusive"),
        "ConsistentAggregate" => new MxExclusiveConsistentAggregate(key, "Exclusive"),
        _ => throw new ArgumentOutOfRangeException(nameof(attribute))
    };

    public static MxRenamed Handle(MxBatchedWriteAggregate c, [WriteAggregate] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedWriteAggregate.Bin))] MxBinOrder bin)
    {
        LastBin = bin;
        return new MxRenamed(c.Name);
    }

    public static MxRenamed Handle(MxBatchedWriteAggregateOfKey c, [WriteAggregate<MxSku>] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedWriteAggregateOfKey.Bin))] MxBinOrder bin)
    {
        LastBin = bin;
        return new MxRenamed(c.Name);
    }

    public static MxRenamed Handle(MxBatchedWriteModel c, [WriteModel] MxSkuOrder order,
        [ReadModel<MxBin>(nameof(MxBatchedWriteModel.Bin))] MxBinOrder bin)
    {
        LastBin = bin;
        return new MxRenamed(c.Name);
    }

    public static MxRenamed Handle(MxBatchedWriteModelOfKey c, [WriteModel<MxSku>] MxSkuOrder order,
        [ReadModel<MxBin>(nameof(MxBatchedWriteModelOfKey.Bin))] MxBinOrder bin)
    {
        LastBin = bin;
        return new MxRenamed(c.Name);
    }

    public static MxRenamed Handle(MxBatchedConsistentAggregate c, [ConsistentAggregate] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedConsistentAggregate.Bin))] MxBinOrder bin)
    {
        LastBin = bin;
        return new MxRenamed(c.Name);
    }

    public static MxRenamed Handle(MxBatchedVersionedWriteAggregate c, [WriteAggregate] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedVersionedWriteAggregate.Bin))] MxBinOrder bin) => new(c.Name);

    public static MxRenamed Handle(MxBatchedVersionedWriteAggregateOfKey c, [WriteAggregate<MxSku>] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedVersionedWriteAggregateOfKey.Bin))] MxBinOrder bin) => new(c.Name);

    public static MxRenamed Handle(MxBatchedVersionedWriteModel c, [WriteModel] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedVersionedWriteModel.Bin))] MxBinOrder bin) => new(c.Name);

    public static MxRenamed Handle(MxBatchedVersionedWriteModelOfKey c, [WriteModel<MxSku>] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedVersionedWriteModelOfKey.Bin))] MxBinOrder bin) => new(c.Name);

    public static MxRenamed Handle(MxBatchedVersionedConsistentAggregate c, [ConsistentAggregate] MxSkuOrder order,
        [ReadAggregate<MxBin>(nameof(MxBatchedVersionedConsistentAggregate.Bin))] MxBinOrder bin) => new(c.Name);

    public static MxRenamed Handle(MxVersionedWriteAggregate c, [WriteAggregate] MxSkuOrder order) => new(c.Name);
    public static MxRenamed Handle(MxVersionedWriteAggregateOfKey c, [WriteAggregate<MxSku>] MxSkuOrder order) => new(c.Name);
    public static MxRenamed Handle(MxVersionedWriteModel c, [WriteModel] MxSkuOrder order) => new(c.Name);
    public static MxRenamed Handle(MxVersionedWriteModelOfKey c, [WriteModel<MxSku>] MxSkuOrder order) => new(c.Name);
    public static MxRenamed Handle(MxVersionedConsistentAggregate c, [ConsistentAggregate] MxSkuOrder order) => new(c.Name);

    public static MxRenamed Handle(MxExclusiveWriteAggregate c,
        [WriteAggregate(LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)] MxSkuOrder order) => new(c.Name);

    public static MxRenamed Handle(MxExclusiveWriteAggregateOfKey c,
        [WriteAggregate<MxSku>(LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)] MxSkuOrder order) => new(c.Name);

    public static MxRenamed Handle(MxExclusiveWriteModel c,
        [WriteModel(LoadStyle = ModelConcurrencyStyle.Exclusive)] MxSkuOrder order) => new(c.Name);

    public static MxRenamed Handle(MxExclusiveWriteModelOfKey c,
        [WriteModel<MxSku>(LoadStyle = ModelConcurrencyStyle.Exclusive)] MxSkuOrder order) => new(c.Name);

    public static MxRenamed Handle(MxExclusiveConsistentAggregate c,
        [ConsistentAggregate(LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)] MxSkuOrder order) => new(c.Name);
}

public record MxDeciderBySku(MxSku Key, string Name);

// [AggregateHandler] marks the method rather than the class: IntegrateWithWolverine() includes every class marked
// with it, conventional discovery or not, which would add this handler to every host here.
public static class MxDeciderHandlers
{
    [AggregateHandler]
    public static MxRenamed Handle(MxDeciderBySku c, MxSkuOrder order) => new(c.Name);
}
