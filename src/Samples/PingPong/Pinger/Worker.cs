#region sample_PingPong_Worker

using Wolverine;
using Messages;

namespace Pinger;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IMessagePublisher _publisher;

    public Worker(ILogger<Worker> logger, IMessagePublisher publisher)
    {
        _logger = logger;
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingNumber = 1;

        while (!stoppingToken.IsCancellationRequested)
        {

            await Task.Delay(1000, stoppingToken);
            _logger.LogInformation("Sending Ping #{Number}", pingNumber);
            await _publisher.PublishAsync(new Ping { Number = pingNumber });
            pingNumber++;
        }
    }
}


#endregion
