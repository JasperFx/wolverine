using Bobcat.Engine;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// The main store is the default: a host with one main event store and any number of ancillary ones (Marten's
// AddMartenStore<T>, Fisher's AddFisherStore<T>, an embedded CritterWatch console's own store) needs no storeName.
// Against fake stores registered the way Marten, Polecat and Fisher register theirs — the main store under the
// product's IDocumentStore, an ancillary store under a marker interface — with no database. The same rules run
// against real Marten and Fisher stores in ancillary_stores.

public class default_main_store
{
    private sealed class Probe(IServiceProvider services, string? storeName = null) : CritterStackScenario(services, storeName)
    {
        public Task<IEventStore> Route(Type? model, params object[] values) => storeForAsync("the step", model, values);
    }

    /// <summary>A fake store with its product's IDocumentStore interface, describing what it owns.</summary>
    private sealed class ProductStore(string name) : FakeEventStore(name), FakeProduct.IDocumentStore, IFakeAncillaryStore
    {
        // The reset convention Marten, Polecat and Fisher share, so ResetAsync runs on a fake
        public ResettableAdvanced Advanced { get; } = new();

        public static ProductStore Owning(string name, Type? aggregate = null)
        {
            var store = new ProductStore(name) { Usage = new EventStoreUsage() };
            if (aggregate is not null)
            {
                store.Usage.Subscriptions.Add(new SubscriptionDescriptor(SubscriptionType.SingleStreamProjection)
                {
                    AggregateType = TypeDescriptor.For(aggregate)
                });
            }

            return store;
        }
    }

    public sealed class ResettableAdvanced
    {
        public Task ResetAllDataAsync(CancellationToken token) => Task.CompletedTask;
    }

    /// <summary>A store of a second product, as when a host runs both Marten and Fisher.</summary>
    private sealed class OtherProductStore(string name) : FakeEventStore(name), OtherProduct.IDocumentStore;

    private static ServiceCollection services() => new();

    // AddMarten / AddPolecat / AddFisher: the store under the product's IDocumentStore, and as IEventStore
    private static ServiceCollection main(ServiceCollection services, ProductStore store)
    {
        services.AddSingleton<FakeProduct.IDocumentStore>(store);
        services.AddSingleton<IEventStore>(store);
        return services;
    }

    // AddMartenStore<T> and friends: the store under its marker only, and as IEventStore
    private static ServiceCollection ancillary(ServiceCollection services, ProductStore store)
    {
        services.AddSingleton<IFakeAncillaryStore>(store);
        services.AddSingleton<IEventStore>(store);
        return services;
    }

    [Fact]
    public async Task one_main_store_beside_an_ancillary_one_is_the_default()
    {
        var app = ProductStore.Owning("main", aggregate: typeof(Wallet));
        var console = ProductStore.Owning("icritterwatchstore", aggregate: typeof(Invoice));
        var provider = ancillary(main(services(), app), console).BuildServiceProvider();

        provider.MainEventStores().ShouldBe([app]);
        provider.IsMainEventStore(console).ShouldBeFalse();
        provider.EventStore().ShouldBeSameAs(app);

        var scenario = new Probe(provider);
        scenario.EventStore.ShouldBeSameAs(app);

        // Unclaimed and untyped steps fall back to the main store; a type the ancillary store claims still goes there
        (await scenario.Route(typeof(Unclaimed))).ShouldBeSameAs(app);
        (await scenario.Route(null)).ShouldBeSameAs(app);
        (await scenario.Route(typeof(Wallet))).ShouldBeSameAs(app);
        (await scenario.Route(typeof(Invoice))).ShouldBeSameAs(console);

        // Explicit addressing is untouched
        provider.EventStore("icritterwatchstore").ShouldBeSameAs(console);
        new Probe(provider, "icritterwatchstore").EventStore.ShouldBeSameAs(console);
        scenario.In("icritterwatchstore").EventStore.ShouldBeSameAs(console);
        scenario.In<IFakeAncillaryStore>().EventStore.ShouldBeSameAs(console);
    }

    [Fact]
    public async Task several_stores_claiming_a_type_fall_back_to_the_main_store_among_them()
    {
        var app = ProductStore.Owning("main", aggregate: typeof(SharedNote));
        var console = ProductStore.Owning("icritterwatchstore", aggregate: typeof(SharedNote));
        var scenario = new Probe(ancillary(main(services(), app), console).BuildServiceProvider());

        (await scenario.Route(typeof(SharedNote))).ShouldBeSameAs(app);
    }

    [Fact]
    public async Task two_main_stores_keep_the_refusal()
    {
        var marten = ProductStore.Owning("main");
        var fisher = new OtherProductStore("main");
        var services = main(default_main_store.services(), marten);
        services.AddSingleton<OtherProduct.IDocumentStore>(fisher);
        services.AddSingleton<IEventStore>(fisher);
        var provider = services.BuildServiceProvider();

        provider.MainEventStores().Count.ShouldBe(2);

        var ex = Should.Throw<InvalidOperationException>(() => provider.EventStore());
        ex.Message.ShouldContain("2 of them are registered as a main store");
        ex.Message.ShouldContain("Pass storeName");
        ex.Message.ShouldContain("In<TStore>()");
        ex.Message.ShouldContain("In(\"name\")");

        var scenario = new Probe(provider);
        Should.Throw<InvalidOperationException>(() => scenario.EventStore);
        var unrouted = await Should.ThrowAsync<SpecCriticalException>(() => scenario.Route(typeof(Unclaimed)));
        unrouted.Message.ShouldContain("names no default store");
    }

    [Fact]
    public async Task ancillary_stores_alone_keep_the_refusal()
    {
        var left = ProductStore.Owning("left");
        var right = ProductStore.Owning("right");
        var provider = ancillary(ancillary(services(), left), right).BuildServiceProvider();

        provider.MainEventStores().ShouldBeEmpty();

        var ex = Should.Throw<InvalidOperationException>(() => provider.EventStore());
        ex.Message.ShouldContain("none of them is registered as the application's main store");
        ex.Message.ShouldContain("'left'");
        ex.Message.ShouldContain("[ancillary]");
        ex.Message.ShouldContain("In<TStore>()");

        var unrouted = await Should.ThrowAsync<SpecCriticalException>(() => new Probe(provider).Route(typeof(Unclaimed)));
        unrouted.Message.ShouldContain("names no default store");
    }

    [Fact]
    public void a_single_store_is_the_default_whatever_its_role()
    {
        var only = ProductStore.Owning("iinvoicingstore");
        var provider = ancillary(services(), only).BuildServiceProvider();

        provider.EventStore().ShouldBeSameAs(only);
        new Probe(provider).EventStore.ShouldBeSameAs(only);
    }

    [Fact]
    public void a_store_with_no_document_store_interface_is_never_a_main_store()
    {
        // A hand-written store named "main" is still not a main store: the role comes from the registration
        var provider = new ServiceCollection()
            .AddSingleton<IEventStore>(new FakeEventStore("main"))
            .AddSingleton<IEventStore>(new FakeEventStore("other"))
            .BuildServiceProvider();

        provider.MainEventStores().ShouldBeEmpty();
        Should.Throw<InvalidOperationException>(() => provider.EventStore());
    }

    // ---- waits ----------------------------------------------------------------------------------

    // Two databases each, so the wait goes to them rather than first asking the read-only view whether it is empty
    private static IEventDatabase[] databases() => [Substitute.For<IEventDatabase>(), Substitute.For<IEventDatabase>()];

    private static void shouldHaveWaited(IEnumerable<IEventDatabase> databases, bool waited)
    {
        foreach (var database in databases)
        {
            if (waited) database.Received().WaitForNonStaleProjectionDataAsync(Arg.Any<TimeSpan>());
            else database.DidNotReceive().WaitForNonStaleProjectionDataAsync(Arg.Any<TimeSpan>());
        }
    }

    [Fact]
    public async Task the_catch_up_waits_on_the_main_store_and_never_on_an_ancillary_store_the_specification_never_touched()
    {
        var app = ProductStore.Owning("main", aggregate: typeof(Wallet));
        var console = ProductStore.Owning("icritterwatchstore", aggregate: typeof(Invoice));
        app.Databases = databases();
        console.Databases = databases();
        // An ancillary store whose daemon never catches up — it is never asked
        foreach (var database in console.Databases)
            database.WaitForNonStaleProjectionDataAsync(Arg.Any<TimeSpan>()).Returns(new TaskCompletionSource().Task);

        var scenario = new Probe(ancillary(main(services(), app), console).BuildServiceProvider());

        await scenario.Route(typeof(Wallet));
        await scenario.AfterAsyncDaemonsCatchUpAsync().WaitAsync(TimeSpan.FromSeconds(5));

        shouldHaveWaited(app.Databases, true);
        shouldHaveWaited(console.Databases, false);
    }

    [Fact]
    public async Task the_catch_up_waits_on_an_ancillary_store_once_the_specification_touches_it()
    {
        var app = ProductStore.Owning("main", aggregate: typeof(Wallet));
        var invoicing = ProductStore.Owning("iinvoicingstore", aggregate: typeof(Invoice));
        app.Databases = databases();
        invoicing.Databases = databases();
        var provider = ancillary(main(services(), app), invoicing).BuildServiceProvider();

        // A step routed to it
        var routed = new Probe(provider);
        await routed.Route(typeof(Invoice));
        await routed.AfterAsyncDaemonsCatchUpAsync();
        shouldHaveWaited(invoicing.Databases, true);

        // A view pinned to it
        invoicing.Databases = databases();
        var viewed = new Probe(provider);
        _ = viewed.In<IFakeAncillaryStore>();
        await viewed.AfterAsyncDaemonsCatchUpAsync();
        shouldHaveWaited(invoicing.Databases, true);

        // ResetAsync forgets what the last specification touched
        invoicing.Databases = databases();
        app.Databases = databases();
        app.ReadOnlyView = Substitute.For<IReadOnlyEventStore>();
        await viewed.ResetAsync();
        await viewed.AfterAsyncDaemonsCatchUpAsync();
        shouldHaveWaited(app.Databases, true);
        shouldHaveWaited(invoicing.Databases, false);
    }

    [Fact]
    public async Task with_no_default_store_the_catch_up_still_waits_on_every_store()
    {
        var left = ProductStore.Owning("left");
        var right = ProductStore.Owning("right");
        left.Databases = databases();
        right.Databases = databases();

        var scenario = new Probe(ancillary(ancillary(services(), left), right).BuildServiceProvider());
        await scenario.AfterAsyncDaemonsCatchUpAsync();

        shouldHaveWaited(left.Databases, true);
        shouldHaveWaited(right.Databases, true);
    }
}

