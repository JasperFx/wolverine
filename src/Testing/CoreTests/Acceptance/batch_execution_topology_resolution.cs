using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Batching;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-3867. Startup decides whether a batch executes on one dedicated local queue or is distributed
/// across the partitioned topology its element type already belongs to.
/// </summary>
public class batch_execution_topology_resolution
{
    private static async Task<IHost> hostWith(Action<WolverineOptions> configure, bool withTopology = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MessagePartitioning.ByMessage<IPartitionedOrderMessage>(x => x.OrderId);

                if (withTopology)
                {
                    opts.MessagePartitioning.PublishToPartitionedLocalMessaging("gh3867resolve", 4,
                        topology => { topology.MessagesImplementing<IPartitionedOrderMessage>(); });
                }

                configure(opts);
            }).StartAsync();
    }

    private static BatchingOptions batchingFor(IHost host)
    {
        return host.GetRuntime().Options.BatchDefinitions.Single(x => x.ElementType == typeof(OrderTouched));
    }

    [Fact]
    public async Task targets_the_topology_slots_when_the_element_type_belongs_to_one()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>());

        var batching = batchingFor(host);

        batching.ExecutionSlots.ShouldNotBeNull();
        batching.ExecutionSlots!.Count.ShouldBe(4);
        batching.ExecutionSlots.Select(x => x.Uri.ToString()).ShouldBe([
            "local://gh3867resolve1/", "local://gh3867resolve2/", "local://gh3867resolve3/", "local://gh3867resolve4/"
        ]);
    }

    [Fact]
    public async Task flags_the_slot_endpoints_so_they_get_an_unbounded_execution_block()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>());

        // Those queues are now their own cascade target, which would deadlock a bounded block.
        batchingFor(host).ExecutionSlots!.ShouldAllBe(x => x.HostsBatchExecution);
    }

    [Fact]
    public async Task swaps_the_default_batcher_for_the_group_id_batcher()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>());

        // Slotting is only coherent if a batch belongs to exactly one group.
        batchingFor(host).Batcher.ShouldBeOfType<GroupIdMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task leaves_an_application_supplied_batcher_alone()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>(b =>
        {
            b.Batcher = new PassthroughOrderBatcher();
        }));

        var batching = batchingFor(host);

        batching.Batcher.ShouldBeOfType<PassthroughOrderBatcher>();

        // It still gets the slots — a custom batcher may well stamp its own group ids, and any batch
        // that arrives without one falls back to the dedicated queue.
        batching.ExecutionSlots.ShouldNotBeNull();
    }

    [Fact]
    public async Task naming_the_execution_queue_opts_out()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>(b =>
        {
            b.LocalExecutionQueueName = "gh3867-explicit";
        }));

        var batching = batchingFor(host);

        batching.IsLocalQueueExplicit.ShouldBeTrue();
        batching.ExecutionSlots.ShouldBeNull();
    }

    [Fact]
    public async Task execute_on_dedicated_local_queue_opts_out()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>(b =>
        {
            b.ExecuteOnDedicatedLocalQueue();
        }));

        batchingFor(host).ExecutionSlots.ShouldBeNull();
    }

    [Fact]
    public async Task no_topology_means_the_original_single_queue_behavior()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>(), withTopology: false);

        var batching = batchingFor(host);

        batching.ExecutionSlots.ShouldBeNull();

        // And the batcher is untouched, so nothing changes for the overwhelming majority of users.
        batching.Batcher.ShouldBeOfType<DefaultMessageBatcher<OrderTouched>>();
    }

    [Fact]
    public async Task wolverines_own_default_queue_name_is_not_treated_as_an_explicit_choice()
    {
        using var host = await hostWith(opts => opts.BatchMessagesOf<OrderTouched>());

        var batching = batchingFor(host);

        batching.LocalExecutionQueueName.ShouldNotBeNull();
        batching.IsLocalQueueExplicit.ShouldBeFalse();
    }
}

internal class PassthroughOrderBatcher : IMessageBatcher
{
    public IEnumerable<Envelope> Group(IReadOnlyList<Envelope> envelopes)
    {
        yield return new Envelope(envelopes.Select(x => x.Message).OfType<OrderTouched>().ToArray(), envelopes);
    }

    public Type BatchMessageType => typeof(OrderTouched[]);
}
