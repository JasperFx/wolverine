using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-4662. An inline, bounded retry around the first durable write of an envelope that has no other
/// home yet — the inbox row behind <c>StoreAndForwardAsync</c> on a durable local queue, and the outbox
/// row behind a durable sending agent.
/// </summary>
/// <remarks>
/// <para>
/// These writes used to go through a JasperFx <c>RetryBlock</c>, which runs one attempt inline and then
/// queues the rest to its own worker — so <c>PostAsync</c> completes <i>successfully</i> the moment the
/// first attempt fails, and the caller has already returned by the time the block gives up and discards
/// the envelope with a single line at Information. For an outage longer than the ~400 ms retry window
/// that was silent message loss: no inbox row, no broker copy, nothing for recovery to find, and a
/// caller that cannot tell a published message from a lost one.
/// </para>
///
/// <para>
/// A <c>RetryBlock</c> is the right shape once an envelope is durable somewhere — something else can
/// recover it. It is the wrong shape for the write that makes it durable in the first place, because
/// there is no other copy to fall back on. So the same attempt budget is kept, awaited inline, and the
/// last exception is allowed to reach the caller, which is what the EF Core and database outbox paths
/// already do for the same outage.
/// </para>
/// </remarks>
internal static class DurableWriteRetry
{
    /// <summary>
    /// The JasperFx RetryBlock defaults these writes previously inherited, preserved so a transient
    /// blip still costs the caller latency rather than an exception.
    /// </summary>
    private static readonly TimeSpan[] Pauses = [50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds()];

    internal static async Task ExecuteAsync(Func<Task> write, Envelope envelope, ILogger logger,
        CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await write().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (attempt < Pauses.Length && !cancellation.IsCancellationRequested)
            {
                logger.LogWarning(e,
                    "Attempt {Attempt} of {Total} to persist {Envelope} failed, retrying in {Pause}",
                    attempt + 1, Pauses.Length + 1, envelope, Pauses[attempt]);

                await Task.Delay(Pauses[attempt], cancellation).ConfigureAwait(false);
            }
        }
    }
}
