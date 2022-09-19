using Baseline.Dates;
using Wolverine;
using OtelMessages;

namespace OtelWebApi;

public static class InitialCommandHandler
{
    public static async Task Handle(InitialCommand cmd, IMessagePublisher publisher)
    {
        await Task.Delay(100.Milliseconds());

        await publisher.InvokeAsync(new LocalMessage1(cmd.Name));

        await Task.Delay(50.Milliseconds());

        await publisher.EnqueueAsync(new LocalMessage2(cmd.Name));

        await publisher.PublishAsync(new TcpMessage1(cmd.Name));

        await publisher.PublishAsync(new RabbitMessage1{Name = cmd.Name});

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
