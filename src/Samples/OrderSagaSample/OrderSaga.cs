using Baseline.Dates;
using Wolverine;

namespace OrderSagaSample;

#region sample_Order_saga

public record StartOrder(string OrderId);

public record CompleteOrder(string Id);

public record OrderTimeout(string Id) : TimeoutMessage(1.Minutes());

public class Order : Saga
{
    public string? Id { get; set; }

    // This method would be called when a StartOrder message arrives
    // to start a new Order
    public OrderTimeout Start(StartOrder order, ILogger<Order> logger)
    {
        Id = order.OrderId; // defining the Saga Id.

        logger.LogInformation("Got a new order with id {Id}", order.OrderId);
        // creating a timeout message for the saga
        return new OrderTimeout(order.OrderId);
    }

    // Apply the CompleteOrder to the saga
    public void Handle(CompleteOrder complete, ILogger<Order> logger)
    {
        logger.LogInformation("Completing order {Id}", complete.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    // Delete this order if it has not already been deleted to enforce a "timeout"
    // condition
    public void Handle(OrderTimeout timeout, ILogger<Order> logger)
    {
        logger.LogInformation("Applying timeout to order {Id}", timeout.Id);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }
}


#endregion
