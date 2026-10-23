using Wolverine;

namespace AspireWithRabbitMq;

// IMessageBus is scoped, so a hosted service resolves one from its own scope
public class PingerService(IServiceScopeFactory scopes, ILogger<PingerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var number = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            await bus.SendAsync(new PingMessage(++number));
            logger.LogInformation("Sent ping #{Number}", number);
        }
    }
}
