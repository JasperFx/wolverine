using Wolverine;

namespace AspireWithRabbitMq;

public class PingerService(IMessageBus bus, ILogger<PingerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var number = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            await bus.SendAsync(new PingMessage(++number));
            logger.LogInformation("Sent ping #{Number}", number);
        }
    }
}
