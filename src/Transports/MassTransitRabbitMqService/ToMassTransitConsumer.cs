using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.Internals;

namespace MassTransitService;

public class ToMassTransitConsumer : IConsumer<ToExternal>
{
    public static List<ToExternal> Received = new();

    private static TaskCompletionSource<ToExternal> _completion = new();

    public static Task WaitForReceipt()
    {
        return _completion.Task.OrTimeout(60000);
    }

    public Task Consume(ConsumeContext<ToExternal> context)
    {
        Received.Add(context.Message);

        _completion.SetResult(context.Message);
        return Task.CompletedTask;
    }
}
