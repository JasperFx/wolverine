using System;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

// GH-3618: the store-aware TryRebuildRegisteredProjectionAsync overload is keyed on an event store's
// identity. A caller that resolved an agent URI through FindAgentUriAsync should be able to derive that key
// from the URI it already holds instead of re-implementing the URI grammar, which is what StoreIdentityOf is
// for. Its output has to agree exactly with how the family keys its own stores, or a store-scoped rebuild
// silently finds nothing.
public class event_subscription_family_store_identity_of
{
    [Fact]
    public void extracts_the_store_identity_from_an_agent_uri()
    {
        var uri = EventSubscriptionAgentFamily.UriFor(
            new EventStoreIdentity("main", "Marten"),
            new DatabaseId("localhost", "postgres"),
            ShardName.Compose("Trip", "All"));

        EventSubscriptionAgentFamily.StoreIdentityOf(uri).ShouldBe("main:marten");
    }

    [Fact]
    public void agrees_with_the_identity_string_the_family_keys_stores_by()
    {
        // The family keys _stores on EventStoreIdentity.ToString() (case-normalized), and BuildAgentAsync
        // reconstructs that same key from the URI. StoreIdentityOf must land on the identical value.
        var identity = new EventStoreIdentity("orders", "SqlServer");

        var uri = EventSubscriptionAgentFamily.UriFor(identity, new DatabaseId("localhost", "orders"),
            ShardName.Compose("Order", "All"));

        EventSubscriptionAgentFamily.StoreIdentityOf(uri)
            .ShouldBe(identity.ToString().ToLowerInvariant());
    }

    [Fact]
    public void is_case_insensitive_about_a_store_type_with_upper_case_letters()
    {
        // System.Uri lowercases the authority while EventStoreIdentity preserves casing (GH-3168), so a
        // store type like Polecat's "SqlServer" has to normalize the same way on both sides.
        var uri = EventSubscriptionAgentFamily.UriFor(
            new EventStoreIdentity("Orders", "SqlServer"),
            new DatabaseId("localhost", "orders"),
            ShardName.Compose("Order", "All"));

        EventSubscriptionAgentFamily.StoreIdentityOf(uri).ShouldBe("orders:sqlserver");
    }

    [Fact]
    public void returns_null_for_a_uri_that_is_not_this_scheme()
    {
        EventSubscriptionAgentFamily.StoreIdentityOf(new Uri("rabbitmq://queue/incoming")).ShouldBeNull();
    }

    [Fact]
    public void returns_null_when_there_is_no_store_name_segment()
    {
        EventSubscriptionAgentFamily.StoreIdentityOf(new Uri("event-subscriptions://marten")).ShouldBeNull();
    }

    [Fact]
    public async Task a_store_this_family_does_not_manage_reports_no_match()
    {
        // So a caller looping over every registered family can move on to the next one rather than
        // treating "wrong family" as "rebuild failed".
        var family = new EventSubscriptionAgentFamily([], []);

        (await family.TryRebuildRegisteredProjectionAsync("nobody:marten", "Trip:All", null,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_null_or_empty_store_identity_reports_no_match()
    {
        var family = new EventSubscriptionAgentFamily([], []);

        (await family.TryRebuildRegisteredProjectionAsync("", "Trip:All", null, CancellationToken.None))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task find_agent_uri_for_a_store_this_family_does_not_manage_reports_nothing()
    {
        // GH-3647: the store-scoped FindAgentUriAsync overload must return null rather than falling back to a
        // store-blind search, so a caller looping over every registered family moves on to the next one.
        var family = new EventSubscriptionAgentFamily([], []);

        (await family.FindAgentUriAsync("nobody:marten", "Trip:All", null, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task find_agent_uri_with_an_empty_store_identity_reports_nothing()
    {
        var family = new EventSubscriptionAgentFamily([], []);

        (await family.FindAgentUriAsync("", "Trip:All", null, CancellationToken.None)).ShouldBeNull();
    }
}
