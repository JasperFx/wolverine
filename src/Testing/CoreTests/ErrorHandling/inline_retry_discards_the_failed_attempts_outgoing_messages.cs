using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling;

// IMessageBus.InvokeAsync() retries a failing handler inline. The queued execution path discards the
// failed attempt's outgoing messages (MessageContext.ClearAllAsync) before it retries, and the inline
// path must too -- otherwise the messages published by an attempt whose work is being redone are sent
// at the final flush alongside the retry's own, duplicating (or inventing) messages.
public class inline_retry_discards_the_failed_attempts_outgoing_messages
{
    public enum Policy
    {
        RetryOnce,
        RetryWithCooldown
    }

    private static async Task<IHost> startHostAsync(Policy policy, InvokeTracingMode tracing,
        InlineRetryAttempts attempts)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<InlineRetryHandler>();
                opts.Discovery.IncludeType<InlineRetryObserver>();

                opts.InvokeTracing = tracing;
                opts.Services.AddSingleton(attempts);

                var expression = opts.Policies.OnException<InlineRetryTransientException>();
                if (policy == Policy.RetryOnce)
                {
                    expression.RetryOnce();
                }
                else
                {
                    expression.RetryWithCooldown(10.Milliseconds(), 10.Milliseconds());
                }
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(Policy.RetryOnce, InvokeTracingMode.Lightweight)]
    [InlineData(Policy.RetryWithCooldown, InvokeTracingMode.Lightweight)]
    [InlineData(Policy.RetryOnce, InvokeTracingMode.Full)]
    [InlineData(Policy.RetryWithCooldown, InvokeTracingMode.Full)]
    public async Task only_the_messages_of_the_successful_attempt_are_sent(Policy policy, InvokeTracingMode tracing)
    {
        var attempts = new InlineRetryAttempts();
        using var host = await startHostAsync(policy, tracing, attempts);

        var tracked = await host.InvokeMessageAndWaitAsync(new InlineRetryTrigger(), 10000);

        attempts.Count.ShouldBe(2);

        // Attempt 1 published a message before it failed; that work was rolled back, so only the
        // message from attempt 2 may go out
        tracked.Sent.MessagesOf<InlineRetryPublished>().Select(x => x.Attempt).ShouldBe([2]);
        tracked.Executed.MessagesOf<InlineRetryPublished>().Select(x => x.Attempt).ShouldBe([2]);
        attempts.Observed.ShouldBe([2]);
    }

    [Fact]
    public async Task a_message_that_exhausts_its_inline_retries_sends_nothing()
    {
        var attempts = new InlineRetryAttempts { FailAlways = true };
        using var host = await startHostAsync(Policy.RetryOnce, InvokeTracingMode.Lightweight, attempts);

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(async Task (IMessageContext c) =>
            {
                await Should.ThrowAsync<InlineRetryTransientException>(async () =>
                    await c.InvokeAsync(new InlineRetryTrigger()));
            });

        attempts.Count.ShouldBe(2);

        attempts.Observed.ShouldBeEmpty();
    }
}

public record InlineRetryTrigger;

public record InlineRetryPublished(int Attempt);

public class InlineRetryTransientException : Exception;

public class InlineRetryAttempts
{
    private int _count;
    private readonly List<int> _observed = [];

    public bool FailAlways { get; init; }

    public int Count => _count;

    public int[] Observed
    {
        get
        {
            lock (_observed) return _observed.ToArray();
        }
    }

    public int Next() => Interlocked.Increment(ref _count);

    public void Observe(int attempt)
    {
        lock (_observed) _observed.Add(attempt);
    }
}

[WolverineIgnore]
public class InlineRetryHandler
{
    public async Task Handle(InlineRetryTrigger message, IMessageBus bus, InlineRetryAttempts attempts)
    {
        var attempt = attempts.Next();
        await bus.PublishAsync(new InlineRetryPublished(attempt));

        if (attempts.FailAlways || attempt == 1)
        {
            throw new InlineRetryTransientException();
        }
    }
}

[WolverineIgnore]
public class InlineRetryObserver
{
    public void Handle(InlineRetryPublished message, InlineRetryAttempts attempts)
    {
        attempts.Observe(message.Attempt);
    }
}
