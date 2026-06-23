using CoreTests.Runtime;
using NSubstitute;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

// GH-3226: when an event-store-backed Main (Marten/Polecat IntegrateWithWolverine) is combined with a
// database-backed transport that also registers an implicit Main store, the app has two 'Main' stores.
// An opt-in DurabilitySettings.ResolveMainStoreOnConflict callback can designate the Main and demote the
// rest to Ancillary instead of Wolverine throwing.
public class reconcile_competing_main_stores_3226
{
    private static IMessageStore MainStore(string uri)
    {
        var store = Substitute.For<IMessageStore>();
        store.Uri.Returns(new Uri(uri));
        store.Role.Returns(MessageStoreRole.Main);
        // Mirror MessageDatabase.DemoteToAncillary, which flips the reported role.
        store.When(x => x.DemoteToAncillary()).Do(_ => store.Role.Returns(MessageStoreRole.Ancillary));
        return store;
    }

    [Fact]
    public async Task throws_on_multiple_mains_when_no_resolver_is_configured()
    {
        var eventStore = MainStore("wolverinedb://eventstore/main");
        var transport = MainStore("sqlserver://queue/control");

        var collection = new MessageStoreCollection(new MockWolverineRuntime(), [eventStore, transport], []);

        await Should.ThrowAsync<InvalidWolverineStorageConfigurationException>(
            () => collection.InitializeAsync().AsTask());
    }

    [Fact]
    public async Task resolver_keeps_the_chosen_main_and_demotes_the_others()
    {
        var eventStore = MainStore("wolverinedb://eventstore/main");
        var transport = MainStore("sqlserver://queue/control");

        var runtime = new MockWolverineRuntime();
        runtime.Options.Durability.ResolveMainStoreOnConflict =
            mains => mains.Single(x => x.Uri.Scheme == "wolverinedb");

        var collection = new MessageStoreCollection(runtime, [eventStore, transport], []);

        await Should.NotThrowAsync(() => collection.InitializeAsync().AsTask());

        collection.Main.ShouldBeSameAs(eventStore);
        transport.Role.ShouldBe(MessageStoreRole.Ancillary);
        transport.Received().DemoteToAncillary();
        eventStore.DidNotReceive().DemoteToAncillary();
    }

    [Fact]
    public async Task resolver_returning_null_falls_back_to_the_strict_validation()
    {
        var eventStore = MainStore("wolverinedb://eventstore/main");
        var transport = MainStore("sqlserver://queue/control");

        var runtime = new MockWolverineRuntime();
        runtime.Options.Durability.ResolveMainStoreOnConflict = _ => null;

        var collection = new MessageStoreCollection(runtime, [eventStore, transport], []);

        await Should.ThrowAsync<InvalidWolverineStorageConfigurationException>(
            () => collection.InitializeAsync().AsTask());
    }
}
