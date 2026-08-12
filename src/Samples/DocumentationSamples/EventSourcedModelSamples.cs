using JasperFx.Events.Tags;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace DocumentationSamples.EventSourcedModels;

public record OrderShipped(DateTimeOffset When);

public record OrderItemReady(string Item);

public record ShipOrder(Guid OrderId);

public record MarkItemReady(Guid OrderId, string Item);

public record ReadOrderStatus(Guid OrderId);

public record OrderStatusReport(bool IsShipped);

public class Order
{
    public Guid Id { get; set; }
    public bool Shipped { get; set; }
    public List<string> ReadyItems { get; } = [];

    public void Apply(OrderShipped _) => Shipped = true;
    public void Apply(OrderItemReady e) => ReadyItems.Add(e.Item);
}

#region sample_using_write_model_attribute

public static class ShipOrderHandler
{
    // [WriteModel] loads the Order's event stream with concurrency protection, hands you
    // the current state, and appends whatever events you return back to that same stream.
    // Nothing here names an event store -- the same handler is valid on Marten or Polecat.
    public static OrderShipped Handle(ShipOrder command, [WriteModel] Order order)
    {
        return new OrderShipped(DateTimeOffset.UtcNow);
    }
}

#endregion

#region sample_using_decider_function_attribute

// [DeciderFunction] is the method (or class) level form: it reads the model's identity off the
// incoming command -- MarkItemReady.OrderId here -- rather than off a marked parameter. The name
// is the functional event sourcing one: decide(command, state) -> events.
[DeciderFunction]
public static class MarkItemReadyHandler
{
    public static OrderItemReady Handle(MarkItemReady command, Order order)
    {
        return new OrderItemReady(command.Item);
    }
}

#endregion

#region sample_using_read_model_attribute

public static class ReadOrderStatusHandler
{
    // [ReadModel] resolves the model's current state through the store's FetchLatest() API.
    // No stream lock is taken, and Wolverine does not expect any events back.
    public static OrderStatusReport Handle(ReadOrderStatus query, [ReadModel] Order order)
    {
        return new OrderStatusReport(order.Shipped);
    }
}

#endregion

#region sample_write_model_with_exclusive_locking

public static class ShipOrderExclusivelyHandler
{
    public static OrderShipped Handle(ShipOrder command,
        [WriteModel(LoadStyle = ModelConcurrencyStyle.Exclusive)] Order order)
    {
        return new OrderShipped(DateTimeOffset.UtcNow);
    }
}

#endregion

public record ReserveSeat(Guid ScreeningId, Guid CustomerId);

public record SeatReserved(Guid ScreeningId, Guid CustomerId);

public class SeatAvailability
{
    public Guid Id { get; set; }
    public int SeatsLeft { get; set; }
}

#region sample_using_dcb_model_attribute

public static class ReserveSeatHandler
{
    // A Dynamic Consistency Boundary is not one stream, so you say which events it spans with a
    // Load() (or Before()) method returning an EventTagQuery. Wolverine passes it to the store's
    // FetchForWritingByTags<T>().
    public static EventTagQuery Load(ReserveSeat command)
        => EventTagQuery.For(command.ScreeningId).Or(command.CustomerId);

    // [DcbModel] hands you the model projected from every event the query matched, and appends
    // what you return through the boundary -- with the store checking at commit that no matching
    // event has been written since. Nothing here names an event store.
    public static SeatReserved Handle(ReserveSeat command, [DcbModel] SeatAvailability availability)
    {
        if (availability.SeatsLeft <= 0)
        {
            throw new InvalidOperationException("The screening is sold out");
        }

        return new SeatReserved(command.ScreeningId, command.CustomerId);
    }
}

#endregion
