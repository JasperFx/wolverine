using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// GH-5695. MultipleHandlerBehavior.Separated gives every handler type its own chain and local queue. A message of a partitioned topology
/// received at a slot with no chain bound to it is relayed to its handler types' local queues, and each of those
/// queues re-routed it straight back to the slot because it is not a slot itself, forever, with no handler
/// running and no error.
/// </summary>
public class partitioned_local_topology_with_separated_handlers_5695
{
    /// <summary>
    /// Two handler types each run on their own queue, so the topology could no longer keep the order of a group's
    /// messages even without the relay loop. Startup refuses it instead of failing silently.
    /// </summary>
    [Fact]
    public async Task a_partitioned_message_with_two_handler_types_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery()
                        .IncludeType(typeof(BillingOrderPlacedHandler5695))
                        .IncludeType(typeof(ShippingOrderPlacedHandler5695));
                    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                    opts.MessagePartitioning
                        .ByMessage<IOrderEvent5695>(x => x.OrderId.ToString())
                        .PublishToPartitionedLocalMessaging("orders5695", 4,
                            t => t.MessagesImplementing<IOrderEvent5695>());
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(OrderPlaced5695));
        ex.Message.ShouldContain(nameof(BillingOrderPlacedHandler5695));
        ex.Message.ShouldContain(nameof(ShippingOrderPlacedHandler5695));
    }

    /// <summary>
    /// The default combined mode runs both handler types in one chain on the slot, which keeps the group in order, so
    /// it is not refused
    /// </summary>
    [Fact]
    public async Task combined_handlers_in_a_partitioned_topology_still_run_once_each()
    {
        CombinedOrderPlacedHandlers5695.Calls.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CombinedBillingHandler5695))
                    .IncludeType(typeof(CombinedShippingHandler5695));

                opts.MessagePartitioning
                    .ByMessage<IOrderEvent5695>(x => x.OrderId.ToString())
                    .PublishToPartitionedLocalMessaging("combined5695", 4,
                        t => t.MessagesImplementing<IOrderEvent5695>());
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity(10.Seconds()).PublishMessageAndWaitAsync(new OrderPlaced5695(Guid.NewGuid()));

        CombinedOrderPlacedHandlers5695.Calls.OrderBy(x => x).ShouldBe(["billing", "shipping"]);
    }

    /// <summary>
    /// The relay loop itself, on a configuration startup still accepts: the message's only handler is sticky to a
    /// local queue outside the topology, so the slot relays it there, and that queue has to run the chain bound to
    /// it instead of sending it back to the slot.
    /// </summary>
    [Fact]
    public async Task the_queue_a_slot_relays_to_runs_its_own_chain_instead_of_re_routing()
    {
        StickyOrderShippedHandler5695.Handled.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StickyOrderShippedHandler5695));

                opts.LocalQueue("shipping5695");

                opts.MessagePartitioning
                    .ByMessage<IOrderEvent5695>(x => x.OrderId.ToString())
                    .PublishToPartitionedLocalMessaging("shipped5695", 4,
                        t => t.MessagesImplementing<IOrderEvent5695>());
            }).StartAsync(TestContext.Current.CancellationToken);

        var orderId = Guid.NewGuid();
        var session = await host.TrackActivity(10.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new OrderShipped5695(orderId));

        StickyOrderShippedHandler5695.Handled.ShouldBe([orderId]);

        // A loop sends the same message thousands of times in the tracking window
        session.Sent.MessagesOf<OrderShipped5695>().Count().ShouldBeLessThan(5);
    }
}

public interface IOrderEvent5695
{
    Guid OrderId { get; }
}

public record OrderPlaced5695(Guid OrderId) : IOrderEvent5695;

public record OrderShipped5695(Guid OrderId) : IOrderEvent5695;

public class BillingOrderPlacedHandler5695
{
    public void Handle(OrderPlaced5695 message)
    {
    }
}

public class ShippingOrderPlacedHandler5695
{
    public void Handle(OrderPlaced5695 message)
    {
    }
}

[StickyHandler("shipping5695")]
public class StickyOrderShippedHandler5695
{
    public static ConcurrentQueue<Guid> Handled { get; } = new();

    public void Handle(OrderShipped5695 message) => Handled.Enqueue(message.OrderId);
}

public static class CombinedOrderPlacedHandlers5695
{
    public static ConcurrentQueue<string> Calls { get; } = new();
}

public class CombinedBillingHandler5695
{
    public void Handle(OrderPlaced5695 message) => CombinedOrderPlacedHandlers5695.Calls.Enqueue("billing");
}

public class CombinedShippingHandler5695
{
    public void Handle(OrderPlaced5695 message) => CombinedOrderPlacedHandlers5695.Calls.Enqueue("shipping");
}
