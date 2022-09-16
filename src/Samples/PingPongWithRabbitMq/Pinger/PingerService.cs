using Baseline.Dates;
using Wolverine;

namespace Pinger;

// Just a simple IHostedService object that will publish
// a new PingMessage every second
public class PingerService : IHostedService
{
    // IMessagePublisher is an interface you can use
    // strictly to publish messages through Wolverine
    private readonly IMessagePublisher _publisher;

    public PingerService(IMessagePublisher publisher)
    {
        _publisher = publisher;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var count = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var message = new PingMessage
                {
                    Number = ++count
                };

                await _publisher.SendAsync(message);

                await Task.Delay(1.Seconds(), cancellationToken);
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
