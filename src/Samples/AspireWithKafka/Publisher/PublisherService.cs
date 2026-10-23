using Wolverine;

namespace AspireWithKafka;

// IMessageBus is scoped, so a hosted service resolves one from its own scope
public class PublisherService(IServiceScopeFactory scopes, ILogger<PublisherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

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
