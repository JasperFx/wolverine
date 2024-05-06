using Marten;

namespace DocumentationSamples;

public class CompoundHandlerSamples;

public class Order
{
    public Guid Id { get; set; }
}

public record ShipOrder(Guid OrderId, Guid CustomerId);

public class MissingOrderException : Exception
{
    public MissingOrderException(Guid commandOrderId)
    {
        throw new NotImplementedException();
    }
}

public class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

#region sample_ShipOrderHandler

public static class ShipOrderHandler
{
    // This would be called first
    public static async Task<(Order, Customer)> LoadAsync(ShipOrder command, IDocumentSession session)
    {
        var order = await session.LoadAsync<Order>(command.OrderId);
        if (order == null)
        {
            throw new MissingOrderException(command.OrderId);
        }

        var customer = await session.LoadAsync<Customer>(command.CustomerId);

        return (order, customer);
    }

    // By making this method completely synchronous and having it just receive the
    // data it needs to make determinations of what to do next, Wolverine makes this
    // business logic easy to unit test
    public static IEnumerable<object> Handle(ShipOrder command, Order order, Customer customer)
    {
        // use the command data, plus the related Order & Customer data to 
        // "decide" what action to take next

        yield return new MailOvernight(order.Id);
    }
}

#endregion

public record MailOvernight(Guid OrderId);