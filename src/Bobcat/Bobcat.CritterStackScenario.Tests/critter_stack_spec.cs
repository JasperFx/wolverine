using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// <see cref="CritterStackSpec" /> is the base class a Marten- or Fisher-only suite derives from: the whole
/// vocabulary as its own members, delegated to one <see cref="CritterStackScenario" />.
/// </summary>
public abstract class a_spec_class<THost>(THost store) : CritterStackSpec(store.IsAvailable ? store.Host : new NoHost())
    where THost : StoreHost
{
    [Fact]
    public async Task a_spec_class_reads_given_when_then()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenEvents<Wallet>(id, new WalletOpened(id, "Ivo"));
            await WhenActing("the wallet is credited",
                services => EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, [new WalletCredited(id, 4)]));
            ThenEvents(Specify<WalletCredited>().With(x => x.Amount, 4m));
            await ThenAggregate<Wallet>(id, Specify<Wallet>().With(x => x.Balance, 4m));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => x.Keyword).ShouldBe(["Given", "When", "Then", "And"]);
        LastAct.NewEvents.ShouldHaveSingleItem();
        ProjectionTimeout.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task a_spec_class_addresses_a_store_by_name()
    {
        Assert.SkipUnless(store.IsAvailable, store.SkipReason);
        var id = Guid.NewGuid();

        // bobcat#504: the view shares the spec's specification, so the act sees the stream it arranged
        await In("main").GivenEvents<Wallet>(id, new WalletOpened(id, "Jo"));
        await WhenActing("the wallet is credited",
            services => EventStoreAuthoring.AppendAsync(services.EventStore(), typeof(Wallet), id, [new WalletCredited(id, 2)]));

        In("main").EventStore.ShouldBeSameAs(EventStore);
        LastAct.NewEvents.ShouldHaveSingleItem();
        LastAct.ForeignEvents.ShouldBeEmpty();
    }

    // Stands in for a host when the store is not available here, so the class constructs and the test skips
    private sealed class NoHost : Microsoft.Extensions.Hosting.IHost
    {
        public IServiceProvider Services { get; } = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}

public class a_spec_class_on_marten(MartenStoreHost store) : a_spec_class<MartenStoreHost>(store), IClassFixture<MartenStoreHost>;

public class a_spec_class_on_fisher(FisherStoreHost store) : a_spec_class<FisherStoreHost>(store), IClassFixture<FisherStoreHost>;
