using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// The durable inbox's ordinary life: a message arrives, its handler commits an effect, its row is settled. Run for
/// every store over every transport; each concrete subclass is bodyless and names its own feature.
/// </summary>
/// <remarks>
/// #5119 lived here: on SQS every handled message's row was left 'Incoming', owned by the live node, forever. These
/// specs would have caught it on the first run.
/// </remarks>
public abstract class SettlingCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected SettlingCompliance(StoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        the = new DurableSteps(store, transport, output);

        // Inline retries, then the default: dead letter
        the.ConfigureNodes(opts => opts.OnException<RigFailure>().RetryTimes(3));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => the.DisposeAsync();

    [BobcatSpec]
    public async Task every_handled_message_settles_its_inbox_row()
    {
        await the.ANodeStarts();
        the.MessagesToSend(20);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_few_hundred_concurrent_messages_all_settle()
    {
        // A database-backed queue takes about a minute for 300 messages
        the.SettleTimeout = 3.Minutes();

        await the.ANodeStarts();
        the.MessagesToSend(300);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_message_retried_after_a_failure_commits_once_and_settles()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 2);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    [BobcatSpec]
    public async Task a_failure_after_the_effect_is_written_rolls_the_effect_back()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsBeforeCommit(2, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>
    /// GH-5123. Two copies of one message in one micro-batch, neither in the inbox yet. The batched insert reported the
    /// duplicate as a raw unique violation, so the receiver paused the listener for an "unavailable" inbox. Each copy
    /// should simply be one run and one duplicate.
    /// </summary>
    [BobcatSpec]
    public async Task two_copies_of_each_message_arriving_together_commit_once_without_stopping_the_listener()
    {
        await the.ANodeStarts();
        the.MessagesToSend(10);

        await the.EachMessageIsSentTwice(1);

        await the.TheDurableInvariantsHold();
        the.TheListenerNeverStopped(1);
    }

    [BobcatSpec]
    public async Task a_message_that_always_fails_is_dead_lettered_and_settled()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(4);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }
}
