using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Local;

/// <summary>
/// GH-5518. Wolverine 7 gives each handler type of a message type with several handler types a local queue named for
/// the handler type, while a message type with one handler type keeps its message type's queue. The
/// NameQueuesByHandlerType() opt-in names the local queue of every handler type that is not sticky for the handler
/// type, so the queue names don't change when a message type picks up a second handler type later.
/// </summary>
public class naming_local_queues_by_handler_type_5518
{
    private static async Task<IHost> hostWith(bool nameQueuesByHandlerType, params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);

                if (nameQueuesByHandlerType)
                {
                    #region sample_name_local_queues_by_handler_type

                    // Name the local queue of every handler type that is not sticky
                    // for the handler type, even when it's the only handler of its message type
                    opts.Policies.ConfigureConventionalLocalRouting().NameQueuesByHandlerType();

                    #endregion
                }
            }).StartAsync();
    }

    private static Uri queueOf<THandler>() => new("local://" + typeof(THandler).FullNameInCode().ToLowerInvariant());

    private static Uri messageTypeQueueOf<TMessage>() => new("local://" + typeof(TMessage).ToMessageTypeName().ToLowerInvariant());

    private static Uri[] localRoutesFor<TMessage>(IHost host) => host.GetRuntime().RoutingFor(typeof(TMessage)).Routes
        .OfType<MessageRoute>().Select(x => x.Uri).ToArray();

    [Fact]
    public async Task off_by_default_a_message_type_with_one_handler_type_uses_the_message_types_queue()
    {
        using var host = await hostWith(false, typeof(ShipOrderHandler5518));

        localRoutesFor<ShipOrder5518>(host).ShouldBe([messageTypeQueueOf<ShipOrder5518>()]);
    }

    [Fact]
    public async Task a_message_type_with_one_handler_type_uses_the_handler_types_queue()
    {
        using var host = await hostWith(true, typeof(ShipOrderHandler5518));

        localRoutesFor<ShipOrder5518>(host).ShouldBe([queueOf<ShipOrderHandler5518>()]);

        var message = new ShipOrder5518(Guid.NewGuid());
        var session = await host.SendMessageAndWaitAsync(message);

        session.Executed.Envelopes().Single(x => Equals(x.Message, message)).Destination
            .ShouldBe(queueOf<ShipOrderHandler5518>());

        OrderHandlers5518.HandledBy(message.Id).ShouldBe([typeof(ShipOrderHandler5518)]);
    }

    [Fact]
    public async Task a_message_type_with_two_handler_types_uses_the_same_queues_as_without_the_option()
    {
        using var host = await hostWith(true, typeof(SendConfirmationHandler5518), typeof(ReserveStockHandler5518));

        localRoutesFor<OrderPlaced5518>(host).ShouldBe(
            [queueOf<SendConfirmationHandler5518>(), queueOf<ReserveStockHandler5518>()], ignoreOrder: true);

        var message = new OrderPlaced5518(Guid.NewGuid());
        await host.SendMessageAndWaitAsync(message);

        OrderHandlers5518.HandledBy(message.Id).ShouldBe(
            [typeof(SendConfirmationHandler5518), typeof(ReserveStockHandler5518)], ignoreOrder: true);
    }

    [Fact]
    public async Task a_sticky_handler_stays_on_its_own_endpoint()
    {
        using var host = await hostWith(true, typeof(AuditOrderHandler5518));

        localRoutesFor<AuditOrder5518>(host).ShouldBe([new Uri("local://audit-5518")]);
    }

    [Fact]
    public async Task local_queue_attribute_on_the_message_type_still_names_the_queue()
    {
        using var host = await hostWith(true, typeof(InvoiceOrderHandler5518));

        localRoutesFor<InvoiceOrder5518>(host).ShouldBe([new Uri("local://invoices-5518")]);
    }

    [Fact]
    public async Task local_queue_for_the_message_type_configures_the_handler_types_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ShipOrderHandler5518>();

                opts.Policies.ConfigureConventionalLocalRouting().NameQueuesByHandlerType();
                opts.LocalQueueFor<ShipOrder5518>().Sequential();
            }).StartAsync(TestContext.Current.CancellationToken);

        var queue = host.GetRuntime().Options.Transports.GetOrCreate<LocalTransport>()
            .QueueFor(typeof(ShipOrderHandler5518).FullNameInCode());

        queue.MaxDegreeOfParallelism.ShouldBe(1);
        localRoutesFor<ShipOrder5518>(host).ShouldBe([queue.Uri]);
    }

    [Fact]
    public async Task a_message_on_the_message_types_old_queue_is_still_handled()
    {
        // What a node without the option left behind: messages addressed to the message type's own local queue.
        // The message type's one handler runs them wherever they are received
        using var host = await hostWith(true, typeof(ShipOrderHandler5518));

        var message = new ShipOrder5518(Guid.NewGuid());
        var oldQueue = messageTypeQueueOf<ShipOrder5518>();

        var session = await host.TrackActivity()
            .ExecuteAndWaitAsync(c => c.EndpointFor(oldQueue).SendAsync(message).AsTask());

        session.Executed.Envelopes().Single(x => Equals(x.Message, message)).Destination.ShouldBe(oldQueue);
        OrderHandlers5518.HandledBy(message.Id).ShouldBe([typeof(ShipOrderHandler5518)]);
    }
}

public record ShipOrder5518(Guid Id);

public record OrderPlaced5518(Guid Id);

public record AuditOrder5518;

[LocalQueue("invoices-5518")]
public record InvoiceOrder5518;

public static class OrderHandlers5518
{
    private static readonly List<(Type HandlerType, Guid Id)> _handled = [];

    public static void Record(Type handlerType, Guid id)
    {
        lock (_handled) _handled.Add((handlerType, id));
    }

    public static Type[] HandledBy(Guid id)
    {
        lock (_handled) return _handled.Where(x => x.Id == id).Select(x => x.HandlerType).ToArray();
    }
}

// Hidden from conventional discovery so they cannot leak into every other host in this assembly
[WolverineIgnore]
public class ShipOrderHandler5518
{
    public void Handle(ShipOrder5518 message) => OrderHandlers5518.Record(GetType(), message.Id);
}

[WolverineIgnore]
public class SendConfirmationHandler5518
{
    public void Handle(OrderPlaced5518 message) => OrderHandlers5518.Record(GetType(), message.Id);
}

[WolverineIgnore]
public class ReserveStockHandler5518
{
    public void Handle(OrderPlaced5518 message) => OrderHandlers5518.Record(GetType(), message.Id);
}

[WolverineIgnore]
[StickyHandler("audit-5518")]
public class AuditOrderHandler5518
{
    public void Handle(AuditOrder5518 message)
    {
    }
}

[WolverineIgnore]
public class InvoiceOrderHandler5518
{
    public void Handle(InvoiceOrder5518 message)
    {
    }
}
