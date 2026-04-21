using Wolverine;

namespace AspireWithKafka;

public class PublisherService(IMessageBus bus, ILogger<PublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var orderId = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            var order = new OrderPlaced(Guid.NewGuid(), $"PRODUCT-{orderId++}", 1);
            await bus.PublishAsync(order);
            logger.LogInformation("Published order {OrderId}", order.OrderId);
        }
    }
}
