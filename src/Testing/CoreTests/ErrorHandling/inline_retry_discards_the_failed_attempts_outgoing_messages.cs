using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.RemoteInvocation;
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
                opts.Discovery.IncludeType<InlineRetryAckHandler>();
                opts.Discovery.IncludeType<InlineRetryStaleResponseHandler>();
                opts.Discovery.IncludeType<InlineRetrySaga>();

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

    [Theory]
    [InlineData(Policy.RetryOnce, InvokeTracingMode.Lightweight)]
    [InlineData(Policy.RetryWithCooldown, InvokeTracingMode.Lightweight)]
    [InlineData(Policy.RetryOnce, InvokeTracingMode.Full)]
    [InlineData(Policy.RetryWithCooldown, InvokeTracingMode.Full)]
    public async Task a_requested_acknowledgement_is_still_sent_exactly_once_after_a_retry(Policy policy,
        InvokeTracingMode tracing)
    {
        var attempts = new InlineRetryAttempts();
        using var host = await startHostAsync(policy, tracing, attempts);

        InlineRetryAckResponse? response = null;
        var tracked = await host.TrackActivity()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(async Task (IMessageContext c) =>
            {
                response = await c.InvokeAsync<InlineRetryAckResponse>(new InlineRetryAckTrigger(),
                    new DeliveryOptions { AckRequested = true });
            });

        attempts.Count.ShouldBe(2);
        response.ShouldNotBeNull().Attempt.ShouldBe(2);

        // ReadEnvelope queues the acknowledgement before the first attempt; discarding the failed
        // attempt's output must not discard it, or the retry succeeds without ever sending it
        tracked.Sent.MessagesOf<Acknowledgement>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_requested_acknowledgement_is_sent_once_when_no_retry_is_needed()
    {
        var attempts = new InlineRetryAttempts();
        attempts.Next(); // the handler only fails on its first attempt
        using var host = await startHostAsync(Policy.RetryOnce, InvokeTracingMode.Lightweight, attempts);

        var tracked = await host.TrackActivity()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(async Task (IMessageContext c) =>
            {
                await c.InvokeAsync<InlineRetryAckResponse>(new InlineRetryAckTrigger(),
                    new DeliveryOptions { AckRequested = true });
            });

        tracked.Sent.MessagesOf<Acknowledgement>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_response_cascaded_by_the_failed_attempt_is_not_returned_when_the_retry_produces_none()
    {
        var attempts = new InlineRetryAttempts();
        using var host = await startHostAsync(Policy.RetryOnce, InvokeTracingMode.Lightweight, attempts);

        InlineRetryStaleResponse? response = null;
        await host.TrackActivity()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(async Task (IMessageContext c) =>
            {
                response = await c.InvokeAsync<InlineRetryStaleResponse>(new InlineRetryStaleResponseTrigger());
            });

        attempts.Count.ShouldBe(2);

        // The first attempt assigned Envelope.Response before it failed, and that work was rolled back
        response.ShouldBeNull();
    }

    [Fact]
    public async Task messages_published_early_in_the_retry_do_not_inherit_the_failed_attempts_saga_id()
    {
        var attempts = new InlineRetryAttempts();
        using var host = await startHostAsync(Policy.RetryOnce, InvokeTracingMode.Lightweight, attempts);

        var tracked = await host.InvokeMessageAndWaitAsync(new InlineRetrySagaStart(Guid.NewGuid()), 10000);

        attempts.Count.ShouldBe(2);

        // The saga id is set after Start() runs, so the first attempt leaves it behind on the context.
        // The probe is published from inside Start(), before the retry sets the saga id itself, so on
        // a clean context it carries none, exactly as it did on the first attempt
        var probes = tracked.Sent.RecordsInOrder().Where(x => x.Envelope?.Message is InlineRetrySagaProbe).ToArray();
        probes.Length.ShouldBe(1);
        probes[0].Envelope!.SagaId.ShouldBeNull();
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

    public void Handle(InlineRetrySagaProbe message)
    {
    }
}

public record InlineRetryAckTrigger;

public record InlineRetryAckResponse(int Attempt);

[WolverineIgnore]
public class InlineRetryAckHandler
{
    public async Task<InlineRetryAckResponse> Handle(InlineRetryAckTrigger message, IMessageBus bus,
        InlineRetryAttempts attempts)
    {
        var attempt = attempts.Next();
        await bus.PublishAsync(new InlineRetryPublished(attempt));

        if (attempt == 1)
        {
            throw new InlineRetryTransientException();
        }

        return new InlineRetryAckResponse(attempt);
    }
}

public record InlineRetryStaleResponseTrigger;

public record InlineRetryStaleResponse(int Attempt);

// Applied as part of a cascade, the way a transactional postprocessor runs after the response was
// already captured: it fails the attempt once the response has been assigned
public class InlineRetryFailsWhenApplied : ISendMyself
{
    public ValueTask ApplyAsync(IMessageContext context) => throw new InlineRetryTransientException();
}

[WolverineIgnore]
public class InlineRetryStaleResponseHandler
{
    public IEnumerable<object> Handle(InlineRetryStaleResponseTrigger message, InlineRetryAttempts attempts)
    {
        if (attempts.Next() == 1)
        {
            return [new InlineRetryStaleResponse(1), new InlineRetryFailsWhenApplied()];
        }

        return [];
    }
}

public record InlineRetrySagaStart(Guid Id);

public record InlineRetrySagaProbe;

[WolverineIgnore]
public class InlineRetrySaga : Saga
{
    public Guid Id { get; set; }

    public static async Task<(InlineRetrySaga, InlineRetryFailsWhenApplied?)> Start(InlineRetrySagaStart message,
        IMessageBus bus, InlineRetryAttempts attempts)
    {
        await bus.PublishAsync(new InlineRetrySagaProbe());

        // Only the first attempt fails, and it does so after the saga id has been set on the context
        return (new InlineRetrySaga { Id = message.Id },
            attempts.Next() == 1 ? new InlineRetryFailsWhenApplied() : null);
    }
}
