using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using NSubstitute;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// A projection wait decides WHAT to wait on from the store's configured shards
/// (<c>IEventStore&lt;,&gt;.AllShards()</c>) rather than from the progress table, because a daemon
/// writes a shard's progress row only after its first batch — right after the first append an empty
/// table looks exactly like "no async projections", and a wait keyed on it passes vacuously. That
/// was seen for real against Marten (see <see cref="MartenIntegrationTests"/>).
/// </summary>
public class ConfiguredShardWaitTests
{
    public class Account;

    [Fact]
    public async Task an_empty_progress_table_is_not_mistaken_for_no_projections()
    {
        var (store, _) = storeWithShards(progress: [], "Account:All");

        var ex = await Should.ThrowAsync<TimeoutException>(
            () => EventStores.WaitForProjectionAsync<Account>(store, minSequence: 3, timeout: TimeSpan.FromMilliseconds(150)));

        ex.Message.ShouldContain("'Account:All' at 0");
    }

    [Fact]
    public async Task no_configured_shards_means_nothing_to_wait_for()
    {
        var (store, database) = storeWithShards(progress: []);

        await EventStores.WaitForProjectionAsync<Account>(store, minSequence: 99, timeout: TimeSpan.FromMilliseconds(150));

        await database.DidNotReceive().AllProjectionProgress(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task a_name_matching_no_configured_shard_is_a_wiring_error()
    {
        var (store, _) = storeWithShards(progress: [], "Orders:All", "Billing:All");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => EventStores.WaitForProjectionAsync<Account>(store, minSequence: 1, timeout: TimeSpan.FromMilliseconds(150)));

        ex.Message.ShouldContain("'Orders:All'");
        ex.Message.ShouldContain("'Billing:All'");
    }

    [Fact]
    public async Task returns_once_the_configured_shard_reports_the_sequence()
    {
        var (store, _) = storeWithShards(progress: [new ShardState("Account:All", 5), new ShardState("Other:All", 0)], "Account:All", "Other:All");

        await EventStores.WaitForProjectionAsync<Account>(store, minSequence: 5, timeout: TimeSpan.FromMilliseconds(500));
    }

    private static (IEventStore Store, IEventDatabase Database) storeWithShards(IReadOnlyList<ShardState> progress, params string[] shardIdentities)
    {
        var database = Substitute.For<IEventDatabase>();
        database.Identifier.Returns("main");
        database.AllProjectionProgress(Arg.Any<CancellationToken>()).Returns(Task.FromResult(progress));

        var store = Substitute.For<IEventStore<EventStoresTests.IFakeSession, EventStoresTests.IFakeSession>>();
        store.AllDatabases().Returns(new ValueTask<IReadOnlyList<IEventDatabase>>([database]));

        IReadOnlyList<AsyncShard<EventStoresTests.IFakeSession, EventStoresTests.IFakeSession>> shards = shardIdentities
            .Select(identity => identity.Split(':'))
            .Select(parts => new AsyncShard<EventStoresTests.IFakeSession, EventStoresTests.IFakeSession>(
                new AsyncOptions(),
                default,
                new ShardName(parts[0], parts[1], 1),
                Substitute.For<ISubscriptionFactory<EventStoresTests.IFakeSession, EventStoresTests.IFakeSession>>(),
                null!))
            .ToList();
        store.AllShards().Returns(shards);

        return (store, database);
    }
}
