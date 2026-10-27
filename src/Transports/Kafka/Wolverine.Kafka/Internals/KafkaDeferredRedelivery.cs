using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// GH-5236. Kafka has no way to hand a delivery back to the broker, so a deferred envelope is offered to the
/// receiver again. That used to happen inline, inside the receiver's own call to DeferAsync(). When the
/// durable inbox write kept failing, receive, fail, defer and receive again recursed until the process died
/// of a stack overflow. The envelope now goes back to the receiver on its own task, after a short pause, and
/// only while the listener is still running. Once the listener stops (including a pause for inbox recovery),
/// the envelope is left alone. Its offset was never completed, so the committed position can't move past it,
/// and the restarted consumer reads it again.
/// </summary>
internal static class KafkaDeferredRedelivery
{
    /// <summary>
    /// How long a deferred envelope waits before it is offered to the receiver again. This bounds how fast
    /// a receiver that keeps deferring (a failing inbox, a latched receiver) can spin.
    /// </summary>
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(1);

    public static ValueTask DeferAsync(IListener listener, IReceiver receiver, Envelope envelope,
        CancellationToken stopping, ILogger logger)
    {
        if (stopping.IsCancellationRequested) return ValueTask.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Delay, stopping).ConfigureAwait(false);
                await receiver.ReceivedAsync(listener, envelope).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The listener stopped. The uncommitted offset brings the envelope back on restart
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error redelivering deferred message {MessageId} at {Uri}", envelope.Id,
                    listener.Address);
            }
        }, CancellationToken.None);

        return ValueTask.CompletedTask;
    }
}
