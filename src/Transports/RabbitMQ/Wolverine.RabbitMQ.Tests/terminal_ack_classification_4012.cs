using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-4012 item 5. RabbitMQ was the one transport that already classified a terminal settle failure, but it
/// did so with a <c>catch</c> inside the block's own callback -- which works, and hides the give-up from the
/// block: a swallowed exception is indistinguishable from success at the block's boundary, so the block can
/// neither log it differently nor hand it to <c>OnTerminalFailure</c>.
///
/// <para>
/// The classification itself is unchanged, and that is the point of these assertions. The previous catch
/// swallowed <b>every</b> <see cref="AlreadyClosedException"/> and made only the log and the GH-3950 quiesce
/// conditional on the delivery tag, so treating just the unknown-tag case as terminal would have been a
/// behaviour change wearing a refactor's clothes.
/// </para>
/// </summary>
public class terminal_ack_classification_4012
{
    private static RabbitMqChannelCallback theCallback()
    {
        return new RabbitMqChannelCallback(NullLogger.Instance, CancellationToken.None, 3);
    }

    [Fact]
    public void the_settle_block_classifies_a_closed_channel_as_terminal()
    {
        using var callback = theCallback();

        callback.Complete.ShouldRetry.ShouldNotBeNull(
            "Without this the block retries every failure, which is what burned the budget before GH-4012.");

        callback.Complete.ShouldRetry!(
                new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 406,
                    "PRECONDITION_FAILED - unknown delivery tag 1")))
            .ShouldBeFalse("The tag belongs to a channel that is gone; no retry on a later channel can succeed.");
    }

    /// <summary>
    /// The half that is easy to get wrong on a refactor: the old catch was on the exception TYPE, not on the
    /// message, so a closed channel is terminal whatever closed it. Narrowing this to the unknown-tag text
    /// would silently start retrying failures that used to stop immediately.
    /// </summary>
    [Fact]
    public void any_closed_channel_is_terminal_not_just_an_unknown_tag()
    {
        using var callback = theCallback();

        callback.Complete.ShouldRetry!(
                new AlreadyClosedException(new ShutdownEventArgs(ShutdownInitiator.Peer, 320,
                    "CONNECTION_FORCED - broker forced connection closure")))
            .ShouldBeFalse();
    }

    [Fact]
    public void an_ordinary_failure_is_still_retried()
    {
        using var callback = theCallback();

        callback.Complete.ShouldRetry!(new TimeoutException("the broker was slow"))
            .ShouldBeTrue("A transient failure is exactly what the retry budget exists for.");
    }

    [Fact]
    public void the_give_up_is_reportable_rather_than_swallowed()
    {
        using var callback = theCallback();

        // The capability the catch-in-the-callback shape could not provide.
        callback.Complete.OnTerminalFailure.ShouldNotBeNull();
    }
}
