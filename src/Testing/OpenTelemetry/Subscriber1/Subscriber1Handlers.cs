using Baseline.Dates;
using Wolverine;
using OtelMessages;

namespace Subscriber1;

public class Subscriber1Handlers
{
    public static async Task Handle(TcpMessage1 cmd, IMessageContext context)
    {
        await Task.Delay(100.Milliseconds());

        await context.RespondToSenderAsync(new TcpMessage2(cmd.Name));
    }

    public static async Task<RabbitMessage2> Handle(RabbitMessage1 message)
    {
        await Task.Delay(100.Milliseconds());
        return new RabbitMessage2 { Name = message.Name };
    }
}
