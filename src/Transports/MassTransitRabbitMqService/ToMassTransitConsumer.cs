using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransit;
using MassTransit.Internals;

namespace MassTransitService;

public class ToMassTransitConsumer : IConsumer<ToExternal>
{
    public static List<ToExternal> Received = new();

    private static readonly TaskCompletionSource<ToExternal> _completion = new();

    public Task Consume(ConsumeContext<ToExternal> context)
    {
        Received.Add(context.Message);

        _completion.SetResult(context.Message);
        return Task.CompletedTask;
    }

    public static Task WaitForReceipt()
    {
        return _completion.Task.OrTimeout(60000);
    }
}