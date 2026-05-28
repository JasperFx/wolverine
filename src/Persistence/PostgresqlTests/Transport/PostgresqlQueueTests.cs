using JasperFx.Core;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Postgresql.Transport;

namespace PostgresqlTests.Transport;

public class PostgresqlQueueTests
{
    private readonly PostgresqlTransport theTransport = new PostgresqlTransport();

    [Fact]
    public void build_uri()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.Name.ShouldBe("one");
        queue.Uri.ShouldBe(new Uri("postgresql://one"));
    }

    [Fact]
    public void must_implement_database_backed_listener()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        (queue is IDatabaseBackedEndpoint).ShouldBeTrue();
    }

    [Fact]
    public void mode_can_be_durable_or_buffered()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.Mode = EndpointMode.Durable;
        queue.Mode = EndpointMode.BufferedInMemory;
    }

    [Fact]
    public void mode_defaults_to_durable()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public void mode_cannot_be_inline()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            var queue = new PostgresqlQueue("one", theTransport);
            queue.Mode = EndpointMode.Inline;
        });
    }

    [Fact]
    public void polling_interval_defaults_to_null()
    {
        var queue = new PostgresqlQueue("one", theTransport);
        queue.PollingInterval.ShouldBeNull();
    }

    [Fact]
    public void short_queue_name_leaves_identifiers_untouched()
    {
        var queue = new PostgresqlQueue("orders", theTransport);

        // Baseline for the GH-2942 regression below - short names are unchanged so callers
        // who already worked keep their existing table/index identifiers.
        queue.QueueTable.Identifier.Name.ShouldBe("wolverine_queue_orders");
        queue.ScheduledTable.Identifier.Name.ShouldBe("wolverine_queue_orders_scheduled");
        queue.ScheduledTable.Indexes.Single().Name
            .ShouldBe("idx_wolverine_queue_orders_scheduled_execution_time");
    }

    [Fact]
    public void long_queue_name_shortens_identifiers_to_fit_namedatalen()
    {
        // GH-2942: PostgreSQL silently truncates identifiers longer than NAMEDATALEN (63 chars).
        // Before the fix the in-memory ScheduledMessageTable.Indexes[0].Name was the full
        // `idx_wolverine_queue_{long}_scheduled_execution_time` while the DB stored the truncated
        // form, so a subsequent ApplyAllConfiguredChangesToDatabaseAsync diff thought the index
        // was missing - the user's "Missing known broker resources" symptom.
        const string longQueueName = "this_is_a_very_long_queue_name_for_a_test";

        var queue = new PostgresqlQueue(longQueueName, theTransport);

        queue.QueueTable.Identifier.Name.Length.ShouldBeLessThanOrEqualTo(63);
        queue.ScheduledTable.Identifier.Name.Length.ShouldBeLessThanOrEqualTo(63);
        queue.ScheduledTable.Indexes.Single().Name.Length.ShouldBeLessThanOrEqualTo(63);
    }

    [Fact]
    public void distinct_long_queue_names_do_not_collide_on_truncated_identifiers()
    {
        // PostgresqlIdentifier.Shorten() appends a deterministic 4-char FNV-1a hash when it has
        // to truncate, so two distinct long names that share a long prefix still map to distinct
        // shortened identifiers - this is the property that makes the previous test safe to land
        // without surprising users who happen to have name collisions.
        const string nameA =
            "this_is_a_really_long_queue_name_with_a_unique_suffix_alpha_one";
        const string nameB =
            "this_is_a_really_long_queue_name_with_a_unique_suffix_beta_two";

        var queueA = new PostgresqlQueue(nameA, theTransport);
        var queueB = new PostgresqlQueue(nameB, theTransport);

        queueA.QueueTable.Identifier.Name.ShouldNotBe(queueB.QueueTable.Identifier.Name);
        queueA.ScheduledTable.Identifier.Name.ShouldNotBe(queueB.ScheduledTable.Identifier.Name);
        queueA.ScheduledTable.Indexes.Single().Name
            .ShouldNotBe(queueB.ScheduledTable.Indexes.Single().Name);
    }
}