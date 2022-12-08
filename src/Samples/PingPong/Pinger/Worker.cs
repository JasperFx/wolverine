#region sample_PingPong_Worker

using Messages;
using Wolverine;

namespace Pinger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IMessageBus _bus;

    public Worker(ILogger<Worker> logger, IMessageBus bus)
    {
        _logger = logger;
        _bus = bus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingNumber = 1;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            _logger.LogInformation("Sending Ping #{Number}", pingNumber);
            await _bus.PublishAsync(new Ping { Number = pingNumber });
            pingNumber++;
        }
    }
}

#endregion