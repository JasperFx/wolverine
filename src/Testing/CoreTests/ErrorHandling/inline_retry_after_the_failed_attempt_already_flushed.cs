using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling;

// GH-4743, the mirror image of GH-4733. That one stopped a failed inline attempt's messages from leaking
// out alongside the retry's. This one is the other direction: the SUCCESSFUL retry's messages being
// silently dropped.
//
// MessageContext._hasFlushed latches on the first FlushOutgoingMessagesAsync and, before this fix, was
// cleared only by ClearState() on pool return. But handlers flush DURING HandleAsync, not only at the end
// of the invocation -- a Marten commit does it from FlushOutgoingMessagesOnCommit.AfterCommitAsync, and
// generated handlers call it outright (see the "just in case Marten did nothing" flush in the generated
// saga handlers). So an attempt that commits and THEN fails -- a postprocessor, a later cascade's
// ISendMyself.ApplyAsync, a saga concurrency failure after a prior flush -- left the flag set on the
// context the retry loop reuses. The retry then succeeded and the final flush in
// Executor.InvokeInlineAsync hit the MultiFlushMode.OnlyOnce guard, dropping every message it published
// with nothing but a LogWarning to show for it.
//
// This is deliberately expressed without a persistence package: the mechanism is the shared
// MessageContext, and a handler that flushes and then throws reproduces it exactly.
public class inline_retry_after_the_failed_attempt_already_flushed
{
    [Fact]
    public async Task the_successful_attempt_can_still_send_its_messages()
    {
        var attempts = new FlushedAttemptCounter();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(FlushesThenFailsHandler));
                opts.Discovery.IncludeType(typeof(FlushedAttemptObserver));
                opts.Services.AddSingleton(attempts);

                opts.Policies.OnException<FlushedAttemptException>().RetryOnce();
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host.TrackActivity()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(async Task (IMessageContext c) => { await c.InvokeAsync(new FlushedAttemptTrigger()); });

        attempts.Count.ShouldBe(2);

        // Attempt 1's message genuinely escaped: it was flushed before the failure, and no reset can
        // recall a message already handed to a sender. Attempt 2's is the one that used to vanish.
        tracked.Sent.MessagesOf<FlushedAttemptPublished>().Select(x => x.Attempt).OrderBy(x => x)
            .ShouldBe([1, 2]);
    }
}

public record FlushedAttemptTrigger;

public record FlushedAttemptPublished(int Attempt);

public class FlushedAttemptException : Exception;

public class FlushedAttemptCounter
{
    private int _count;
    public int Count => _count;
    public int Next() => Interlocked.Increment(ref _count);
}

[WolverineIgnore]
public static class FlushesThenFailsHandler
{
    public static async Task Handle(FlushedAttemptTrigger message, IMessageContext context,
        FlushedAttemptCounter attempts)
    {
        var attempt = attempts.Next();
        await context.PublishAsync(new FlushedAttemptPublished(attempt));

        if (attempt == 1)
        {
            // What a Marten commit does by way of FlushOutgoingMessagesOnCommit.AfterCommitAsync...
            await ((MessageContext)context).FlushOutgoingMessagesAsync();

            // ...and then something after the commit fails
            throw new FlushedAttemptException();
        }
    }
}

[WolverineIgnore]
public static class FlushedAttemptObserver
{
    public static void Handle(FlushedAttemptPublished message)
    {
    }
}
