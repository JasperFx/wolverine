using System;
using System.Threading.Tasks;
using Wolverine.Transports;

namespace Wolverine.Runtime;

internal class InvocationCallback : IChannelCallback, ISupportNativeScheduling, ISupportDeadLetterQueue
{
    public static readonly InvocationCallback Instance = new();

    private InvocationCallback()
    {
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public Task MoveToErrorsAsync(Envelope envelope, Exception exception)
    {
        return Task.CompletedTask;
    }

    public Task MoveToScheduledUntilAsync(Envelope envelope, DateTimeOffset time)
    {
        return Task.CompletedTask;
    }
}
