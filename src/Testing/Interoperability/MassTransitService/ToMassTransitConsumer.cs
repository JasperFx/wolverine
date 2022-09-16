using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InteropMessages;
using MassTransit;
using MassTransit.Internals;

namespace MassTransitService;

public class ToMassTransitConsumer : IConsumer<ToMassTransit>
{
    public static List<ToMassTransit> Received = new();

    private static TaskCompletionSource<ToMassTransit> _completion = new();

    public static Task WaitForReceipt()
    {
        return _completion.Task.OrTimeout(60000);
    }

    public Task Consume(ConsumeContext<ToMassTransit> context)
    {
        Received.Add(context.Message);

        _completion.SetResult(context.Message);
        return Task.CompletedTask;
    }
}
