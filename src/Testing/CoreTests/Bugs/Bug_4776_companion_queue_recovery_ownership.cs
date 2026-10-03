using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Reproduction for https://github.com/JasperFx/wolverine/issues/4776, at the level of the single predicate
/// every durability agent consults before it recovers a dormant inbox row.
///
/// <para>
/// The behavioural proof lives in <c>PostgresqlTests.Transport.Bug_4776_recovered_partition_slot_rows_run_on_the_owner</c>,
/// against a real sharded topology. This fixture needs no database and no broker, and exists for the half of the
/// defect that is a one-line judgement call: a global partition's companion local queue must NOT be treated as
/// the per-database durability agent's to recover, even though it is a plain <c>local://</c> address that is live
/// on every node.
/// </para>
///
/// <para>
/// The negative control is the more important assertion of the two. GH-3856 is an entire bug about dormant rows
/// on a partitioned durable local queue going unclaimed because the GH-3590 carve-out swallowed them, so
/// broadening that carve-out to cover every local queue -- rather than only a companion queue -- would trade this
/// defect straight back for that one.
/// </para>
/// </summary>
public class Bug_4776_companion_queue_recovery_ownership : IAsyncLifetime
{
    // The slot's external endpoint: exclusive in a real topology, listened to on the node that owns the slot.
    private static readonly Uri TheSlot = "stub://partition-slot-1".ToUri();

    // Its companion local queue. Live on every node, which is the whole trap.
    private static readonly Uri TheCompanionQueue = "local://global-partition-slot-1".ToUri();

    // A durable local queue with no part in any partitioned topology -- the GH-3856 control.
    private static readonly Uri APlainLocalQueue = "local://plain-items".ToUri();

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<CompanionRecoveryMessage>().To(TheSlot);
                opts.LocalQueue("plain-items");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // Exactly what GlobalPartitionedMessageTopology stamps on each external slot. Doing it by hand keeps this
        // off a real broker; the property IS the contract the reverse lookup reads. That the real topology writes
        // this same value is asserted in the PostgresqlTests twin.
        _host.GetRuntime().Endpoints.EndpointFor(TheSlot)!.GlobalPartitionLocalQueueUri = TheCompanionQueue;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void a_companion_queue_is_left_to_the_node_owning_its_slot()
    {
        _host.GetRuntime().Endpoints.ListenerOwnsItsInboxRecovery(TheCompanionQueue).ShouldBeTrue();
    }

    [Fact]
    public void every_other_local_queue_is_still_the_durability_agents_to_recover()
    {
        var endpoints = _host.GetRuntime().Endpoints;

        endpoints.ListenerOwnsItsInboxRecovery(APlainLocalQueue).ShouldBeFalse();
        endpoints.ListenerOwnsItsInboxRecovery("local://never-configured".ToUri()).ShouldBeFalse();

        // And the default local queue, which carries the bulk of ordinary durable traffic
        endpoints.ListenerOwnsItsInboxRecovery("local://default".ToUri()).ShouldBeFalse();
    }
}

public record CompanionRecoveryMessage(string Name);
