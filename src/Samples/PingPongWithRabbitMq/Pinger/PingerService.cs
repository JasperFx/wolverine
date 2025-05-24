using JasperFx.Core;
using Wolverine;

namespace Pinger;

// Just a simple IHostedService object that will publish
// a new PingMessage every second
public class PingerService : IHostedService
{
    // IMessagePublisher is an interface you can use
    // strictly to publish messages through Wolverine
    private readonly IServiceProvider _serviceProvider;

    public PingerService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var count = 0;

        await using var scope= _serviceProvider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var message = new PingMessage
                {
                    Number = ++count
                };

                await bus.SendAsync(message);

                await Task.Delay(1.Seconds(), cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}