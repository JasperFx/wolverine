using JasperFx.Core;
using OtelMessages;
using Wolverine;

namespace OtelWebApi;

public static class InitialCommandHandler
{
    public static async Task Handle(InitialCommand cmd, IMessageBus bus)
    {
        await Task.Delay(100.Milliseconds());

        await bus.InvokeAsync(new LocalMessage1(cmd.Name));

        await Task.Delay(50.Milliseconds());

        await bus.PublishAsync(new LocalMessage2(cmd.Name));

        await bus.PublishAsync(new TcpMessage1(cmd.Name));

        await bus.PublishAsync(new RabbitMessage1 { Name = cmd.Name });
    }

    public static Task Handle(LocalMessage1 message)
    {
        return Task.Delay(100.Milliseconds());
    }

    public static Task Handle(TcpMessage2 cmd)
    {
        return Task.Delay(100.Milliseconds());
    }

    public static Task Handle(LocalMessage2 message)
    {
        return Task.Delay(50.Milliseconds());
    }
}