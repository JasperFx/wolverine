using JasperFx.Core;
using OtelMessages;
using Wolverine;

namespace Subscriber1;

public class Subscriber2Handlers
{
    public static async Task<RabbitMessage3> Handle(RabbitMessage1 message)
    {
        await Task.Delay(50.Milliseconds());
        return new RabbitMessage3 { Name = message.Name };
    }

    public static async Task Handle(RabbitMessage3 message, IMessageBus bus)
    {
        await Task.Delay(100.Milliseconds());
        await bus.PublishAsync(new LocalMessage3(message.Name));
    }

    public async Task Handle(LocalMessage3 message, IMessageBus bus)
    {
        await Task.Delay(75.Milliseconds());
        await bus.PublishAsync(new LocalMessage4(message.Name));
    }

    public Task Handle(LocalMessage4 message)
    {
        return Task.Delay(50.Milliseconds());
    }

    public Task Handle(RabbitMessage2 message)
    {
        return Task.Delay(50.Milliseconds());
    }
}