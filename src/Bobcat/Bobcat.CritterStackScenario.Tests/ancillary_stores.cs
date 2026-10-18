using Bobcat.CritterStack;
using Bobcat.Engine;
using JasperFx.Events;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#504: a specification over an application with a main event store and an ancillary one. Steps go to the
// store whose registrations claim their types (A), In<TStore>() / In(name) address one explicitly (B), and an act
// is observed on every store (C). Each test runs on Marten and on Fisher through the classes at the bottom.

public abstract class ancillary_stores<THost, TMarker>(THost host) where THost : TwoStoreHost where TMarker : class
{
    private CritterStackScenario newScenario(string? storeName = null)
    {
        Assert.SkipUnless(host.IsAvailable, host.SkipReason);
        return new CritterStackScenario(host.Host, storeName);
    }

    private IEventStore main => host.Host.Services.EventStore("main");
    private IEventStore invoicing => host.Host.Services.EventStore(host.AncillaryName);

    private static Task append(IServiceProvider services, string store, Type aggregate, Guid id, params object[] events)
        => EventStoreAuthoring.AppendAsync(services.EventStore(store), aggregate, id, events);

    // ---- A: route by type --------------------------------------------------------------------

    [Fact]
    public async Task givens_and_thens_go_to_the_store_that_registers_their_type()
    {
        // Every step here names a type exactly one store registers, so the types decide
        var scenario = newScenario();
        var wallet = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var loose = Guid.NewGuid();
        var summary = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Ann"));
            await scenario.GivenEventsOn<Invoice>(invoice, new InvoiceRaised(invoice, 40m));
            // No aggregate type: its events say where it goes
            await scenario.GivenEvents(loose, new InvoiceRaised(loose, 5m));
            await scenario.GivenReadModel<InvoiceSummary>(new InvoiceSummary { Id = summary, Raised = 9m });

            await scenario.ThenAggregate<Invoice>(invoice, Specifications.Specify<Invoice>().With(x => x.Amount, 40m));
            await scenario.ThenAggregate<Wallet>(wallet, Specifications.Specify<Wallet>().With(x => x.Owner, "Ann"));
            await scenario.ThenDocument<InvoiceSummary>(summary, Specifications.Specify<InvoiceSummary>().With(x => x.Raised, 9m));
        });

        recording.GatheredFailures().ShouldBeNull();

        (await EventStores.FetchStreamAsync(invoicing, invoice)).Count.ShouldBe(1);
        (await EventStores.FetchStreamAsync(main, invoice)).Count.ShouldBe(0);
        (await EventStores.FetchStreamAsync(main, wallet)).Count.ShouldBe(1);
        (await EventStores.FetchStreamAsync(invoicing, wallet)).Count.ShouldBe(0);
        (await EventStores.FetchStreamAsync(invoicing, loose)).Count.ShouldBe(1);
        (await EventStoreAuthoring.LoadDocumentAsync<InvoiceSummary>(invoicing, summary)).ShouldNotBeNull();
        (await scenario.TheAggregate<Invoice>(invoice))!.Amount.ShouldBe(40m);
    }

    [Fact]
    public async Task with_no_store_name_the_main_store_is_the_default()
    {
        // No storeName: of the two stores only one is the application's main store, so it is the default
        var scenario = newScenario();
        scenario.EventStore.ShouldBeSameAs(main);

        var note = Guid.NewGuid();
        var stray = Guid.NewGuid();
        var invoice = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            // Both stores register SharedNote, no store registers Stray: the main store takes both
            await scenario.GivenReadModel<SharedNote>(new SharedNote { Id = note, Text = "both register it" });
            await scenario.GivenReadModel<Stray>(new Stray { Id = stray });
            // ...while a type the ancillary store registers still goes there
            await scenario.GivenEvents<Invoice>(invoice, new InvoiceRaised(invoice, 1m));

            await scenario.ThenDocument<SharedNote>(note, Specifications.Specify<SharedNote>().With(x => x.Text, "both register it"));
            await scenario.ThenNoReadModel<Unclaimed>();
        });

        recording.GatheredFailures().ShouldBeNull();
        (await EventStoreAuthoring.LoadDocumentAsync<SharedNote>(main, note)).ShouldNotBeNull();
        (await EventStoreAuthoring.LoadDocumentAsync<SharedNote>(invoicing, note)).ShouldBeNull();
        (await EventStoreAuthoring.LoadDocumentAsync<Stray>(main, stray)).ShouldNotBeNull();
        (await EventStores.FetchStreamAsync(invoicing, invoice)).Count.ShouldBe(1);

        // A stream named by its id alone has no type to route by: it is read from the main store
        (await scenario.TheStreamVersion(invoice)).ShouldBe(0);
        (await scenario.In<TMarker>().TheStreamVersion(invoice)).ShouldBe(1);
    }

    [Fact]
    public async Task an_explicit_store_name_still_chooses_the_default_over_the_main_store()
    {
        // The ancillary store as the default: SharedNote is claimed by both, but the default can take it...
        var scenario = newScenario(host.AncillaryName);
        var note = Guid.NewGuid();
        await scenario.GivenReadModel<SharedNote>(new SharedNote { Id = note });
        (await EventStoreAuthoring.LoadDocumentAsync<SharedNote>(invoicing, note)).ShouldNotBeNull();

        // ...and a type only the main store claims still goes to the main store
        var wallet = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Ed"));
        (await EventStores.FetchStreamAsync(main, wallet)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task the_default_store_takes_what_no_store_or_several_stores_claim()
    {
        var scenario = newScenario("main");
        var note = Guid.NewGuid();
        var stray = Guid.NewGuid();
        var invoice = Guid.NewGuid();

        await scenario.GivenReadModel<SharedNote>(new SharedNote { Id = note, Text = "both register it" });
        await scenario.GivenReadModel<Stray>(new Stray { Id = stray });
        // ...while a type one store registers still goes to that store
        await scenario.GivenEvents<Invoice>(invoice, new InvoiceRaised(invoice, 1m));

        (await EventStoreAuthoring.LoadDocumentAsync<SharedNote>(main, note)).ShouldNotBeNull();
        (await EventStoreAuthoring.LoadDocumentAsync<SharedNote>(invoicing, note)).ShouldBeNull();
        (await EventStoreAuthoring.LoadDocumentAsync<Stray>(main, stray)).ShouldNotBeNull();
        (await EventStores.FetchStreamAsync(invoicing, invoice)).Count.ShouldBe(1);

        // A stream named by its id alone is read from the default store; In() names the other
        (await scenario.TheStreamVersion(invoice)).ShouldBe(0);
        (await scenario.In<TMarker>().TheStreamVersion(invoice)).ShouldBe(1);
    }

    // ---- B: explicit addressing --------------------------------------------------------------

    [Fact]
    public async Task in_addresses_a_store_by_marker_or_by_name_within_the_same_specification()
    {
        var scenario = newScenario();
        var note = Guid.NewGuid();
        var other = Guid.NewGuid();
        var invoice = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.In<TMarker>().GivenReadModel<SharedNote>(new SharedNote { Id = note, Text = "invoicing" });
            await scenario.In("main").GivenReadModel<SharedNote>(new SharedNote { Id = other, Text = "main" });
            await scenario.In<TMarker>().ThenDocument<SharedNote>(note, Specifications.Specify<SharedNote>().With(x => x.Text, "invoicing"));
            await scenario.In("MAIN").ThenNoReadModel<SharedNote>(note);

            // An act through a view is the specification's act, and the scenario judges it
            await scenario.In<TMarker>().WhenActing("an invoice is raised",
                services => append(services, host.AncillaryName, typeof(Invoice), invoice, new InvoiceRaised(invoice, 2m)));
            scenario.ThenEvents(new InvoiceRaised(invoice, 2m));
            await scenario.In(host.AncillaryName).ThenEventsOn<Invoice>(invoice, 0, new InvoiceRaised(invoice, 2m));
        });

        recording.GatheredFailures().ShouldBeNull();
        // Every step, through a view or not, renders in the one specification
        recording.Steps.Count.ShouldBeGreaterThanOrEqualTo(7);

        (await EventStoreAuthoring.LoadDocumentAsync<SharedNote>(main, other))!.Text.ShouldBe("main");
        (await scenario.In<TMarker>().TheStreamVersion(invoice)).ShouldBe(1);
        scenario.In<TMarker>().EventStore.ShouldBeSameAs(invoicing);
        scenario.In(host.AncillaryName).EventStore.ShouldBeSameAs(invoicing);
        scenario.In<TMarker>().LastAct.ShouldBeSameAs(scenario.LastAct);

        var unknown = Should.Throw<InvalidOperationException>(() => scenario.In("nope"));
        unknown.Message.ShouldContain(host.AncillaryName);
    }

    // ---- C: every store observed during the act ----------------------------------------------

    [Fact]
    public async Task an_act_on_both_stores_is_observed_on_both_and_reported_by_store()
    {
        var scenario = newScenario();
        var wallet = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var started = Guid.NewGuid();
        await scenario.GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Bo"));
        await scenario.GivenEventsOn<Invoice>(invoice, new InvoiceRaised(invoice, 12m));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await scenario.WhenActing("the invoice is paid from the wallet", async services =>
            {
                await append(services, "main", typeof(Wallet), wallet, new WalletDebited(wallet, 12m));
                await append(services, host.AncillaryName, typeof(Invoice), invoice, new InvoicePaid(invoice));
                await append(services, host.AncillaryName, typeof(Invoice), started, new InvoiceRaised(started, 3m));
            });

            scenario.ThenEventsInAnyOrder(new WalletDebited(wallet, 12m), new InvoicePaid(invoice), new InvoiceRaised(started, 3m));
            scenario.ThenEventsOn<Wallet>(wallet, new WalletDebited(wallet, 12m));
            scenario.ThenEventsOn<Invoice>(invoice, new InvoicePaid(invoice));
            await scenario.ThenEventsOn<Invoice>(invoice, 1, new InvoicePaid(invoice));
            await scenario.ThenStreamIsStartedWithEvents<Invoice>(new InvoiceRaised(started, 3m));
        });

        recording.GatheredFailures().ShouldBeNull();
        scenario.TheStartedStream<Invoice>().ShouldBe(started);

        // Which store each event came from
        var act = scenario.LastAct;
        act.NewEvents.Count.ShouldBe(3);
        act.StoreOf(act.NewEvents.Single(x => x.Data is WalletDebited)).ShouldBeSameAs(main);
        act.StoreOf(act.NewEvents.Single(x => x.Data is InvoicePaid)).ShouldBeSameAs(invoicing);
        act.StoreOf(act.NewEvents.Single(x => x.Data is InvoiceRaised)).ShouldBeSameAs(invoicing);

        // ...and a failing whole-store step says so
        var wrong = await Recordings.RecordAsync(() =>
        {
            scenario.ThenNoEvents();
            return Task.CompletedTask;
        });
        var failure = wrong.GatheredFailures().ShouldNotBeNull();
        failure.ShouldContain("Appended by store — 'main': WalletDebited(");
        failure.ShouldContain($"'{host.AncillaryName}': InvoicePaid(");
    }

    [Fact]
    public async Task the_catch_up_waits_on_every_store_and_a_read_on_the_store_it_reads()
    {
        var scenario = newScenario();
        var wallet = Guid.NewGuid();
        var invoice = Guid.NewGuid();

        await scenario.WhenActing("a wallet is opened and credited, and an invoice raised", async services =>
        {
            await append(services, "main", typeof(Wallet), wallet, new WalletOpened(wallet, "Cy"), new WalletCredited(wallet, 20m));
            await append(services, host.AncillaryName, typeof(Invoice), invoice, new InvoiceRaised(invoice, 20m));
        });

        await scenario.AfterAsyncDaemonsCatchUpAsync();

        // Read straight from each store, with no wait of their own: both daemons have caught up
        (await EventStoreAuthoring.LoadDocumentAsync<WalletSummary>(main, wallet))!.Credits.ShouldBe(1);
        (await EventStoreAuthoring.LoadDocumentAsync<InvoiceSummary>(invoicing, invoice))!.Raised.ShouldBe(20m);

        // A read waits on the store it reads, which here is the ancillary one
        var invoiceId = Guid.NewGuid();
        await scenario.WhenActing("another invoice is raised",
            services => append(services, host.AncillaryName, typeof(Invoice), invoiceId, new InvoiceRaised(invoiceId, 4m)));
        await scenario.ThenReadModel<InvoiceSummary>(invoiceId, Specifications.Specify<InvoiceSummary>().With(x => x.Raised, 4m));

        // A view waits on its own store
        await scenario.In<TMarker>().AfterAsyncDaemonsCatchUpAsync();
    }
}

public class ancillary_stores_on_marten(MartenTwoStoreHost host)
    : ancillary_stores<MartenTwoStoreHost, IInvoicingStore>(host), IClassFixture<MartenTwoStoreHost>;

public class ancillary_stores_on_fisher(FisherTwoStoreHost host)
    : ancillary_stores<FisherTwoStoreHost, IFisherInvoicingStore>(host), IClassFixture<FisherTwoStoreHost>;
