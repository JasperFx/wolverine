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
/// GH-5695. 7.0 gives every handler type its own chain and local queue. A message of a partitioned topology
/// received at a slot with no chain bound to it is relayed to its handler types' local queues, and each of those
/// queues re-routed it straight back to the slot because it is not a slot itself, forever, with no handler
/// running and no error.
/// </summary>
public class partitioned_local_topology_with_separated_handlers_5695
{
    /// <summary>
    /// Two handler types each run on their own queue, so the topology could no longer keep the order of a group's
    /// messages even without the relay loop. #5763 refused it at startup; a buffered slot now runs both handler types
    /// itself, one after the other (see partitioned_slot_runs_every_handler_type_in_order_5695)
    /// </summary>
    [Fact]
    public async Task a_partitioned_message_with_two_handler_types_runs_both_on_the_slot()
    {
        BillingOrderPlacedHandler5695.Handled.Clear();
        ShippingOrderPlacedHandler5695.Handled.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(BillingOrderPlacedHandler5695))
                    .IncludeType(typeof(ShippingOrderPlacedHandler5695));

                opts.MessagePartitioning
                    .ByMessage<IOrderEvent5695>(x => x.OrderId.ToString())
                    .PublishToPartitionedLocalMessaging("orders5695", 4,
                        t => t.MessagesImplementing<IOrderEvent5695>());
            }).StartAsync(TestContext.Current.CancellationToken);

        var orderId = Guid.NewGuid();
        var session = await host.TrackActivity(10.Seconds()).PublishMessageAndWaitAsync(new OrderPlaced5695(orderId));

        BillingOrderPlacedHandler5695.Handled.ShouldBe([orderId]);
        ShippingOrderPlacedHandler5695.Handled.ShouldBe([orderId]);

        // No relay to the handler types' own queues, and so no loop
        session.Sent.MessagesOf<OrderPlaced5695>().Count().ShouldBeLessThan(5);
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
    public static ConcurrentQueue<Guid> Handled { get; } = new();

    public void Handle(OrderPlaced5695 message) => Handled.Enqueue(message.OrderId);
}

public class ShippingOrderPlacedHandler5695
{
    public static ConcurrentQueue<Guid> Handled { get; } = new();

    public void Handle(OrderPlaced5695 message) => Handled.Enqueue(message.OrderId);
}

[StickyHandler("shipping5695")]
public class StickyOrderShippedHandler5695
{
    public static ConcurrentQueue<Guid> Handled { get; } = new();

    public void Handle(OrderShipped5695 message) => Handled.Enqueue(message.OrderId);
}
