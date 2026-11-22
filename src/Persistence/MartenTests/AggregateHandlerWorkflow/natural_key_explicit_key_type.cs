using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
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

/// <summary>
/// [WriteAggregate&lt;TKey&gt;] and [ReadAggregate&lt;TKey&gt;] name the natural key to load by, so a message carrying
/// more than one of an aggregate's keys, or a key marked with [Identity], is unambiguous.
/// </summary>
public class natural_key_explicit_key_type : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public natural_key_explicit_key_type(ITestOutputHelper output)
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
                    m.DatabaseSchemaName = "nk_explicit";
                    m.Projections.Snapshot<NkProduct>(SnapshotLifecycle.Inline);
                    m.Projections.Snapshot<NkShelf>(SnapshotLifecycle.Inline);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            // Only compiled when a test asks for one, so the refused shapes don't stop the host
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(NkExplicitHandler))
                .IncludeType(typeof(NkRefusedHandler));
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await _store.Advanced.Clean.DeleteAllEventDataAsync();
        NkExplicitHandler.LastShelf = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    // One stream the product's and the shelf's lookups both point at, so either key finds the shelf
    private async Task<Guid> placeShelf(NkSku sku, NkShelfCode code)
    {
        var streamId = Guid.NewGuid();
        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkShelf>(streamId, new NkProductListed(sku, "Widget"),
            new NkShelfPlaced(code, sku));
        await session.SaveChangesAsync();
        return streamId;
    }

    private async Task<NkShelf?> loadShelf(Guid streamId)
    {
        await using var query = _store.QuerySession();
        return await query.LoadAsync<NkShelf>(streamId);
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var code = _host.GetRuntime().Handlers.ChainFor<T>()!.SourceCode!;
        _output.WriteLine(code);
        return code;
    }

    private string refusalFor<T>()
    {
        var ex = Should.Throw<Exception>(() => _host.GetRuntime().Handlers.HandlerFor<T>());
        _output.WriteLine(ex.ToString());
        return ex.ToString();
    }

    private static string fetchFor<TKey>()
        => $"FetchForWriting<{typeof(NkShelf).FullNameInCode()}, {typeof(TKey).FullNameInCode()}>(";

    [Fact]
    public async Task loads_by_the_owned_key_when_the_message_carries_both()
    {
        var streamId = await placeShelf(new NkSku("EXPLICIT-1"), new NkShelfCode("A-1"));

        sourceFor<AuditNkShelfByCode>().ShouldContain(fetchFor<NkShelfCode>());

        await _host.InvokeMessageAndWaitAsync(new AuditNkShelfByCode(new NkSku("EXPLICIT-1"), new NkShelfCode("A-1")));
        (await loadShelf(streamId))!.Audits.ShouldBe(1);
    }

    [Fact]
    public async Task loads_by_the_mapped_key_when_the_message_carries_both()
    {
        var streamId = await placeShelf(new NkSku("EXPLICIT-2"), new NkShelfCode("A-2"));

        sourceFor<AuditNkShelfBySku>().ShouldContain(fetchFor<NkSku>());

        // The code is deliberately wrong: only the sku is used
        await _host.InvokeMessageAndWaitAsync(new AuditNkShelfBySku(new NkSku("EXPLICIT-2"), new NkShelfCode("NOPE")));
        (await loadShelf(streamId))!.Audits.ShouldBe(1);
    }

    [Fact]
    public async Task identity_picks_the_member_of_the_named_key_type()
    {
        var streamId = await placeShelf(new NkSku("EXPLICIT-3"), new NkShelfCode("A-3"));

        await _host.InvokeMessageAndWaitAsync(
            new AuditNkShelfMarked(new NkShelfCode("OTHER"), new NkShelfCode("A-3")));
        (await loadShelf(streamId))!.Audits.ShouldBe(1);
    }

    [Fact]
    public async Task the_attribute_names_the_member_of_the_named_key_type()
    {
        var streamId = await placeShelf(new NkSku("EXPLICIT-4"), new NkShelfCode("A-4"));

        await _host.InvokeMessageAndWaitAsync(
            new AuditNkShelfNamed(new NkShelfCode("A-4"), new NkShelfCode("OTHER")));
        (await loadShelf(streamId))!.Audits.ShouldBe(1);
    }

    [Fact]
    public async Task a_nullable_member_matches_the_named_struct_key()
    {
        var streamId = await placeShelf(new NkSku("EXPLICIT-5"), new NkShelfCode("A-5"));

        await _host.InvokeMessageAndWaitAsync(new AuditNkShelfByNullableSku(new NkSku("EXPLICIT-5")));
        (await loadShelf(streamId))!.Audits.ShouldBe(1);
    }

    [Fact]
    public async Task reads_by_the_named_key()
    {
        await placeShelf(new NkSku("EXPLICIT-6"), new NkShelfCode("A-6"));

        sourceFor<ViewNkShelfBySku>()
            .ShouldContain($"FetchLatest<{typeof(NkShelf).FullNameInCode()}, {typeof(NkSku).FullNameInCode()}>(");

        await _host.InvokeMessageAndWaitAsync(new ViewNkShelfBySku(new NkSku("EXPLICIT-6"), new NkShelfCode("NOPE")));
        NkExplicitHandler.LastShelf!.Code.ShouldBe(new NkShelfCode("A-6"));
    }

    [Fact]
    public void identity_on_a_natural_key_member_needs_the_key_type()
    {
        // Without the key type, [Identity] would be taken as the stream id
        var refusal = refusalFor<AuditNkShelfIdentityOnly>();
        refusal.ShouldContain("marked with [Identity]");
        refusal.ShouldContain("[WriteAggregate<NkShelfCode>]");
    }

    [Fact]
    public void a_key_type_the_aggregate_does_not_have_is_refused()
    {
        refusalFor<AuditNkProductByShelfCode>().ShouldContain("has no natural key of type");
    }

    [Fact]
    public void a_nullable_key_type_is_refused()
    {
        refusalFor<AuditNkShelfByNullableKeyType>().ShouldContain("Name MartenTests.AggregateHandlerWorkflow.NkSku instead");
    }

    [Fact]
    public void two_members_of_the_named_key_type_need_a_name()
    {
        refusalFor<AuditNkShelfTwoCodes>().ShouldContain("more than one MartenTests.AggregateHandlerWorkflow.NkShelfCode member");
    }

    [Fact]
    public void the_ambiguity_error_suggests_naming_the_key_type()
    {
        refusalFor<AuditNkShelfAmbiguous>().ShouldContain("[WriteAggregate<TKey>]");
    }
}

public record AuditNkShelfByCode(NkSku Sku, NkShelfCode Code);

public record AuditNkShelfBySku(NkSku Sku, NkShelfCode Code);

public record AuditNkShelfMarked(NkShelfCode Other, [property: Identity] NkShelfCode Code);

public record AuditNkShelfNamed(NkShelfCode Code, NkShelfCode Other);

public record AuditNkShelfByNullableSku(NkSku? Sku);

public record ViewNkShelfBySku(NkSku Sku, NkShelfCode Code);

public record AuditNkShelfIdentityOnly(NkSku Sku, [property: Identity] NkShelfCode Code);

public record AuditNkProductByShelfCode(NkShelfCode Code);

public record AuditNkShelfByNullableKeyType(NkSku? Sku);

public record AuditNkShelfTwoCodes(NkShelfCode First, NkShelfCode Second);

public record AuditNkShelfAmbiguous(NkSku Sku, NkShelfCode Code);

public static class NkExplicitHandler
{
    public static NkShelf? LastShelf;

    #region sample_wolverine_marten_write_aggregate_by_named_natural_key
    // The message carries both of the shelf's keys, so the attribute names the one to load by
    public static NkShelfAudited Handle(AuditNkShelfByCode command, [WriteAggregate<NkShelfCode>] NkShelf shelf)
        => new();
    #endregion

    public static NkShelfAudited Handle(AuditNkShelfBySku command, [WriteAggregate<NkSku>] NkShelf shelf)
        => new();

    public static NkShelfAudited Handle(AuditNkShelfMarked command, [WriteAggregate<NkShelfCode>] NkShelf shelf)
        => new();

    public static NkShelfAudited Handle(AuditNkShelfNamed command,
        [WriteAggregate<NkShelfCode>(nameof(AuditNkShelfNamed.Code))] NkShelf shelf)
        => new();

    public static NkShelfAudited Handle(AuditNkShelfByNullableSku command, [WriteAggregate<NkSku>] NkShelf shelf)
        => new();

    public static void Handle(ViewNkShelfBySku query, [ReadAggregate<NkSku>] NkShelf shelf) => LastShelf = shelf;
}

public static class NkRefusedHandler
{
    public static NkShelfAudited Handle(AuditNkShelfIdentityOnly command, [WriteAggregate] NkShelf shelf) => new();

    // JFXEVT016/017 refuse these two at build time. Suppressed so the codegen check behind them, for code
    // the analyzer never sees, can be tested.
#pragma warning disable JFXEVT016, JFXEVT017
    public static NkProductRestocked Handle(AuditNkProductByShelfCode command,
        [WriteAggregate<NkShelfCode>] NkProduct product) => new(1);

    public static NkShelfAudited Handle(AuditNkShelfByNullableKeyType command,
        [WriteAggregate(NaturalKey = typeof(NkSku?))] NkShelf shelf) => new();
#pragma warning restore JFXEVT016, JFXEVT017

    public static NkShelfAudited Handle(AuditNkShelfTwoCodes command, [WriteAggregate<NkShelfCode>] NkShelf shelf)
        => new();

    public static NkShelfAudited Handle(AuditNkShelfAmbiguous command, [WriteAggregate] NkShelf shelf) => new();
}
