using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests.Sagas;

public class order_saga_example
{
    [Fact]
    public async Task try_out_codegen()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "order_saga");
            }).StartAsync();

        await host.InvokeMessageAndWaitAsync(new StartOrder(Guid.NewGuid().ToString(), DateTime.UtcNow));
    }
}

public record StartOrder(string OrderTestId, DateTime CreatedOnUtc);

public record OrderStep2(string OrderTestId, int RandomNumber);

public record CompleteOrder(string OrderTestId);

public class OrderTest : Saga
{
    [SagaIdentity]
    public string? Id { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public bool IsStep2Completed { get; set; } = false;
    public bool IsStep3Completed { get; set; } = false;

    /// <summary>
    /// This method would be called when a StartOrder message arrives to start a new Order
    /// </summary>
    public static (OrderTest, OrderStep2) Start(StartOrder order, ILogger<OrderTest> logger)
    {
        logger.LogInformation("Got a new order with id {Id}", order.OrderTestId);

        // Return the new saga state and the next message to send
        return (
            new OrderTest { Id = order.OrderTestId, CreatedOnUtc = order.CreatedOnUtc },
            new OrderStep2(order.OrderTestId, 66)
        );
    }

    /// <summary>
    /// Do something in step 2 of the order.
    /// </summary>
    public CompleteOrder Handle(OrderStep2 step2, ILogger<OrderTest> logger)
    {
        logger.LogInformation("Applying step 2 to order {Id}. RandomNumber = {number}", Id, step2.RandomNumber);

        IsStep2Completed = true;

        return new CompleteOrder(Id);
    }

    /// <summary>
    /// Apply the CompleteOrder to the saga. 
    /// </summary>
    public void Handle(CompleteOrder complete, ILogger<OrderTest> logger)
    {
        logger.LogInformation("Completing order {Id}", complete.OrderTestId);

        // That's it, we're done. Delete the saga state after the message is done.
        MarkCompleted();
    }

    /// <summary>
    /// TODO: find out how and when this is called?
    /// </summary>
    public static void NotFound(CompleteOrder order, ILogger<OrderTest> logger)
    {
        logger.LogInformation("Tried to complete order {Id}, but it cannot be found", order.OrderTestId);
    }
}
