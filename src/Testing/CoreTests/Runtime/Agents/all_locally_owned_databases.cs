using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// Coverage for <see cref="EventStoreOwnershipExtensions.AllLocallyOwnedDatabasesAsync" /> — the
/// first-class "which databases does THIS node own" filter over <see cref="IEventStore.AllDatabases" />.
/// Recurring per-node work against a multi-database store (progress queries, telemetry polls, readiness
/// gates) must scope to the databases whose event-subscription agents run locally instead of fanning a
/// connection pool out to every shard database from every node (GH-3340, CritterWatch#791).
/// </summary>
public class all_locally_owned_databases
{
    private static readonly EventStoreIdentity TheIdentity = new("main", "marten");
    private static readonly EventStoreIdentity OtherIdentity = new("other", "marten");

    private readonly IWolverineRuntime _runtime = Substitute.For<IWolverineRuntime>();
    private readonly IAgentRuntime _agents = Substitute.For<IAgentRuntime>();
    private readonly IEventStore _store = Substitute.For<IEventStore>();
    private readonly WolverineOptions _options = new();

    public all_locally_owned_databases()
    {
        _options.Durability.Mode = DurabilityMode.Balanced;
        _runtime.Options.Returns(_options);
        _runtime.Agents.Returns(_agents);
        _store.Identity.Returns(TheIdentity);

        withFamilyRegistered(true);
    }

    private void withFamilyRegistered(bool registered)
    {
        var services = new ServiceCollection();
        if (registered)
        {
            services.AddSingleton(Substitute.For<IEventSubscriptionAgentFamily>());
        }

        _runtime.Services.Returns(services.BuildServiceProvider());
    }

    private static IEventDatabase database(string identifier)
    {
        var db = Substitute.For<IEventDatabase>();
        db.Identifier.Returns(identifier);
        return db;
    }

    private void storeHasDatabases(params IEventDatabase[] databases)
    {
        _store.AllDatabases().Returns(new ValueTask<IReadOnlyList<IEventDatabase>>(databases));
    }

    private void storeUsageDescribes(params (string Server, string Database, string Identifier)[] descriptors)
    {
        var usage = new EventStoreUsage(new Uri("eventstore://main"), _store)
        {
            Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.StaticMultiple,
                Databases = descriptors.Select(d => new DatabaseDescriptor
                {
                    ServerName = d.Server, DatabaseName = d.Database, Identifier = d.Identifier
                }).ToList()
            }
        };

        _store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(usage);
    }

    private void nodeRunsAgentsFor(EventStoreIdentity identity, params (string Server, string Database)[] ids)
    {
        var uris = ids
            .Select(id => EventSubscriptionAgentFamily.UriFor(
                identity, new DatabaseId(id.Server, id.Database), new ShardName("Trip")))
            .ToArray();

        _agents.AllRunningAgentUris().Returns(uris);
    }

    [Fact]
    public async Task a_single_database_store_is_returned_as_is()
    {
        var only = database("main");
        storeHasDatabases(only);

        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.ShouldBe([only]);
    }

    [Fact]
    public async Task solo_mode_owns_every_database()
    {
        // Solo runs the daemon in-process rather than as managed agents, so the running-agent set
        // carries no ownership signal — and the one node owns everything anyway
        _options.Durability.Mode = DurabilityMode.Solo;
        storeHasDatabases(database("shard1"), database("shard2"));

        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.Count.ShouldBe(2);
    }

    [Fact]
    public async Task without_managed_distribution_every_database_is_returned()
    {
        // No IEventSubscriptionAgentFamily registered: ownership does not exist as a concept
        withFamilyRegistered(false);
        storeHasDatabases(database("shard1"), database("shard2"));

        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.Count.ShouldBe(2);
    }

    [Fact]
    public async Task filters_to_the_databases_whose_agents_run_on_this_node()
    {
        var shard1 = database("shard1");
        var shard2 = database("shard2");
        var shard3 = database("shard3");
        storeHasDatabases(shard1, shard2, shard3);
        storeUsageDescribes(
            ("pg1", "db_shard1", "shard1"),
            ("pg1", "db_shard2", "shard2"),
            ("pg2", "db_shard3", "shard3"));

        nodeRunsAgentsFor(TheIdentity, ("pg1", "db_shard1"), ("pg2", "db_shard3"));

        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.ShouldBe([shard1, shard3]);
    }

    [Fact]
    public async Task agents_of_another_store_do_not_count_as_ownership()
    {
        storeHasDatabases(database("shard1"), database("shard2"));
        storeUsageDescribes(("pg1", "db_shard1", "shard1"), ("pg1", "db_shard2", "shard2"));

        // This node runs agents for the same databases, but registered under a DIFFERENT store
        nodeRunsAgentsFor(OtherIdentity, ("pg1", "db_shard1"), ("pg1", "db_shard2"));

        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_node_that_owns_nothing_yet_gets_an_empty_list()
    {
        // Distribution is on but this node has no assignments (startup, or a rebalance in flight):
        // empty is the honest answer, so a readiness gate can wait instead of latching early
        storeHasDatabases(database("shard1"), database("shard2"));
        _agents.AllRunningAgentUris().Returns([]);

        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.ShouldBeEmpty();
    }

    [Fact]
    public async Task fails_open_when_the_store_yields_no_usage_descriptor()
    {
        storeHasDatabases(database("shard1"), database("shard2"));
        nodeRunsAgentsFor(TheIdentity, ("pg1", "db_shard1"));
        _store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(Task.FromResult<EventStoreUsage?>(null));

        // Owned ids exist but cannot be mapped to databases: better every database than silently none
        var owned = await _runtime.AllLocallyOwnedDatabasesAsync(_store);

        owned.Count.ShouldBe(2);
    }
}
