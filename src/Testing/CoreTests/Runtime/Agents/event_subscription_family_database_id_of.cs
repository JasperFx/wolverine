using System;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

// GH-3340: application code (readiness gates, per-node health checks) needs a first-class, public way to
// map an event-subscription agent URI back to the shard database it belongs to, without re-implementing
// the internal URI grammar. EventSubscriptionAgentFamily.DatabaseIdOf is that seam.
public class event_subscription_family_database_id_of
{
    [Fact]
    public void extracts_the_database_id_from_a_per_tenant_agent_uri()
    {
        // event-subscriptions://{type}/{name}/{databaseId}/{shard...} — databaseId is DatabaseId.ToString()
        var uri = EventSubscriptionAgentFamily.UriFor(
            new EventStoreIdentity("main", "Marten"),
            new DatabaseId("shardserver", "shard_17"),
            ShardName.Compose("Trip", "All", "alpha1"));

        var id = EventSubscriptionAgentFamily.DatabaseIdOf(uri);

        id.ShouldBe(new DatabaseId("shardserver", "shard_17"));
    }

    [Fact]
    public void round_trips_a_realistic_sharded_database_id()
    {
        // The production shape: a dotted server host and a plain database name (e.g. one physical
        // Postgres database per shard). This is what BuildAgentAsync parses back, and DatabaseIdOf
        // must agree with it exactly.
        var original = new DatabaseId("shard-cluster.internal:5432", "shard_017");
        var uri = EventSubscriptionAgentFamily.UriFor(
            new EventStoreIdentity("main", "Marten"),
            original,
            ShardName.Compose("Trip", "All"));

        EventSubscriptionAgentFamily.DatabaseIdOf(uri).ShouldBe(original);
    }

    [Fact]
    public void returns_null_for_a_uri_that_is_not_this_scheme()
    {
        EventSubscriptionAgentFamily.DatabaseIdOf(new Uri("rabbitmq://queue/incoming")).ShouldBeNull();
    }

    [Fact]
    public void returns_null_when_there_is_no_database_segment()
    {
        EventSubscriptionAgentFamily.DatabaseIdOf(new Uri("event-subscriptions://marten")).ShouldBeNull();
    }
}
