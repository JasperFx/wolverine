using Bobcat.CritterStack;
using Bobcat.Engine;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#504: how a step's store is chosen, against fake stores that describe themselves — no database. The same
// rules run against real Marten and Fisher stores in ancillary_stores.

public class store_routing
{
    // The protected routing seam, as a derived scenario such as WolverineFx.Bobcat's reaches it
    private sealed class Probe(IServiceProvider services, string? storeName = null) : CritterStackScenario(services, storeName)
    {
        public Task<IEventStore> Route(Type? model, params object[] values) => storeForAsync("the step", model, values);
        public Task<IEventStore> RouteTags(params Type[] tags) => storeForAsync("the step", null, null, tags);
        public IReadOnlyList<IEventStore> Observed => storesToObserve();
    }

    /// <summary>A fake store that also describes its documents, as Marten, Polecat and Fisher do.</summary>
    private sealed class DocumentedStore(string name) : FakeEventStore(name), IDocumentStoreUsageSource
    {
        public DocumentStoreUsage Documents { get; } = new();

        Task<DocumentStoreUsage?> IDocumentStoreUsageSource.TryCreateUsage(CancellationToken token)
            => Task.FromResult<DocumentStoreUsage?>(Documents);
    }

    private static IServiceProvider servicesWith(params IEventStore[] stores)
    {
        var services = new ServiceCollection();
        foreach (var store in stores) services.AddSingleton(store);
        return services.BuildServiceProvider();
    }

    private static TypeDescriptor descriptor(Type type) => TypeDescriptor.For(type);

    private static DocumentedStore storeOwning(string name, Type? aggregate = null, Type? @event = null, Type? document = null,
        Type? tag = null)
    {
        var store = new DocumentedStore(name) { Usage = new EventStoreUsage() };
        if (aggregate is not null)
        {
            store.Usage.Subscriptions.Add(new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
            {
                AggregateType = descriptor(aggregate),
                AppliedEvents = @event is null ? [] : [descriptor(@event)]
            });
        }
        else if (@event is not null)
        {
            store.Usage.RegisteredEventTypes.Add(new EventTypeDescriptor(descriptor(@event), @event.Name, null));
        }

        if (document is not null) store.Documents.Documents.Add(new DocumentMappingDescriptor { DocumentType = descriptor(document) });
        if (tag is not null) store.Usage.TagTypes.Add(new TagTypeDescriptor { TagType = tag.FullName! });
        return store;
    }

    [Fact]
    public async Task a_single_store_is_used_as_before_and_never_asked_what_it_owns()
    {
        var store = new FakeEventStore("main");
        var scenario = new Probe(servicesWith(store));

        (await scenario.Route(typeof(Unclaimed))).ShouldBeSameAs(store);
        (await scenario.Route(null, new InvoiceRaised(Guid.NewGuid(), 1m))).ShouldBeSameAs(store);
        scenario.Observed.ShouldBe([store]);
        store.UsageReads.ShouldBe(0);

        // In() still works on a host with one store
        scenario.In("main").EventStore.ShouldBeSameAs(store);
    }

    [Fact]
    public async Task the_one_store_that_claims_a_type_takes_it()
    {
        var main = storeOwning("main", aggregate: typeof(Wallet), @event: typeof(WalletOpened));
        var invoicing = storeOwning("invoicing", aggregate: typeof(Invoice), @event: typeof(InvoiceRaised), document: typeof(SharedNote));
        var scenario = new Probe(servicesWith(main, invoicing));

        (await scenario.Route(typeof(Invoice))).ShouldBeSameAs(invoicing);
        (await scenario.Route(typeof(Wallet))).ShouldBeSameAs(main);
        (await scenario.Route(typeof(SharedNote))).ShouldBeSameAs(invoicing);

        // No model: the events decide, whole, partial or as a type
        (await scenario.Route(null, new InvoiceRaised(Guid.NewGuid(), 1m))).ShouldBeSameAs(invoicing);
        (await scenario.Route(null, Specifications.Specify<WalletOpened>())).ShouldBeSameAs(main);
        (await scenario.Route(null, typeof(InvoiceRaised))).ShouldBeSameAs(invoicing);

        // An unregistered aggregate type: its events decide
        (await scenario.Route(typeof(Unclaimed), new WalletOpened(Guid.NewGuid(), "x"))).ShouldBeSameAs(main);

        scenario.Observed.ShouldBe([main, invoicing]);
    }

    [Fact]
    public async Task tag_types_decide_too()
    {
        var main = storeOwning("main", tag: typeof(ScreeningId));
        var other = storeOwning("other");
        var scenario = new Probe(servicesWith(main, other));

        (await scenario.RouteTags(typeof(ScreeningId))).ShouldBeSameAs(main);
    }

    [Fact]
    public async Task the_routing_is_read_once_per_specification()
    {
        var main = storeOwning("main", aggregate: typeof(Wallet));
        var invoicing = storeOwning("invoicing", aggregate: typeof(Invoice));
        var scenario = new Probe(servicesWith(main, invoicing));

        await scenario.Route(typeof(Invoice));
        await scenario.Route(typeof(Invoice));
        await scenario.Route(typeof(Wallet));

        main.UsageReads.ShouldBe(1);
        invoicing.UsageReads.ShouldBe(1);
    }

    [Fact]
    public async Task several_or_no_claimants_fall_back_to_the_default_store()
    {
        var main = storeOwning("main", document: typeof(SharedNote));
        var invoicing = storeOwning("invoicing", aggregate: typeof(Invoice), document: typeof(SharedNote));
        var scenario = new Probe(servicesWith(main, invoicing), storeName: "main");

        (await scenario.Route(typeof(SharedNote))).ShouldBeSameAs(main);
        (await scenario.Route(typeof(Unclaimed))).ShouldBeSameAs(main);
        (await scenario.Route(null)).ShouldBeSameAs(main);
        (await scenario.Route(typeof(Invoice))).ShouldBeSameAs(invoicing);
    }

    [Fact]
    public async Task several_claimants_without_the_default_among_them_fail_listing_them()
    {
        var main = storeOwning("main");
        var left = storeOwning("left", document: typeof(SharedNote));
        var right = storeOwning("right", document: typeof(SharedNote));
        var scenario = new Probe(servicesWith(main, left, right), storeName: "main");

        var ex = await Should.ThrowAsync<SpecCriticalException>(() => scenario.Route(typeof(SharedNote)));
        ex.Message.ShouldContain("2 event stores register SharedNote ('left'");
        ex.Message.ShouldContain("'right'");
        ex.Message.ShouldContain("scenario.In<IYourStore>()");
        ex.Message.ShouldNotContain("names no default store");
    }

    [Fact]
    public async Task the_events_narrow_a_model_several_stores_claim()
    {
        var left = storeOwning("left", aggregate: typeof(SharedNote), @event: typeof(InvoiceRaised));
        var right = storeOwning("right", aggregate: typeof(SharedNote), @event: typeof(WalletOpened));
        var scenario = new Probe(servicesWith(left, right));

        (await scenario.Route(typeof(SharedNote), new WalletOpened(Guid.NewGuid(), "x"))).ShouldBeSameAs(right);
        await Should.ThrowAsync<SpecCriticalException>(() => scenario.Route(typeof(SharedNote)));
    }

    [Fact]
    public async Task a_store_that_cannot_describe_itself_owns_nothing()
    {
        var main = new FakeEventStore("main"); // describes itself as null
        var invoicing = storeOwning("invoicing", aggregate: typeof(Invoice));
        var scenario = new Probe(servicesWith(main, invoicing));

        (await scenario.Route(typeof(Invoice))).ShouldBeSameAs(invoicing);
        var ex = await Should.ThrowAsync<SpecCriticalException>(() => scenario.Route(typeof(Wallet)));
        ex.Message.ShouldContain("no event store registers Wallet");
    }

    [Fact]
    public async Task a_view_is_pinned_whatever_its_steps_are_about()
    {
        var main = storeOwning("main", aggregate: typeof(Wallet));
        var invoicing = storeOwning("invoicing", aggregate: typeof(Invoice));
        var scenario = new CritterStackScenario(servicesWith(main, invoicing));

        var view = scenario.In("invoicing");
        view.EventStore.ShouldBeSameAs(invoicing);
        view.In("main").EventStore.ShouldBeSameAs(main);
        view.ProjectionTimeout = TimeSpan.FromSeconds(3);
        scenario.ProjectionTimeout.ShouldBe(TimeSpan.FromSeconds(3));
        main.UsageReads.ShouldBe(0);
    }

    [Fact]
    public void a_marker_that_resolves_to_no_event_store_is_named()
    {
        var scenario = new CritterStackScenario(servicesWith(new FakeEventStore("main"), new FakeEventStore("invoicing")));

        var ex = Should.Throw<InvalidOperationException>(() => scenario.In<IInvoicingStore>());
        ex.Message.ShouldContain("No event store is registered under IInvoicingStore");
        ex.Message.ShouldContain("'invoicing'");
    }

    [Fact]
    public void a_marker_is_matched_by_the_store_named_after_it_when_it_resolves_to_something_else()
    {
        // Fisher registers a proxy under the marker and the unwrapped store as IEventStore, named after the marker
        var ancillary = new FakeEventStore("iinvoicingstore");
        var scenario = new CritterStackScenario(servicesWith(new FakeEventStore("main"), ancillary));

        scenario.In<IInvoicingStore>().EventStore.ShouldBeSameAs(ancillary);
    }
}
