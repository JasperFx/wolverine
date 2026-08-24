using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// GH-4079. UsePulsar() registers a *global* AlwaysMatches failure rule, which sits ahead of every
/// opts.Policies.OnException&lt;T&gt;() rule the user adds afterwards. The rule's continuation source
/// declines (returns null) for anything that is not a PulsarListener, but FailureRule reported the rule
/// as handled anyway, so the decline collapsed to MoveToErrorQueue instead of falling through to the
/// user's rule. Blast radius is every endpoint in a Pulsar-enabled app, Pulsar or not.
/// </summary>
public class global_failure_policies
{
    [Fact]
    public async Task global_retry_policy_applies_to_a_local_queue_in_a_pulsar_enabled_app()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

            opts.Policies.OnException<AttemptCountingException>().RetryTimes(3);

            opts.Services.AddSingleton(new AttemptCounter { SucceedOnAttempt = 3 });
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CountedFailureHandler>();
        });

        await host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new CountedFailure());

        host.Services.GetRequiredService<AttemptCounter>().Count.ShouldBe(3);
    }

    /// <summary>
    /// The control: the identical host with UsePulsar() removed.
    /// </summary>
    [Fact]
    public async Task global_retry_policy_applies_to_a_local_queue_without_pulsar()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.Policies.OnException<AttemptCountingException>().RetryTimes(3);

            opts.Services.AddSingleton(new AttemptCounter { SucceedOnAttempt = 3 });
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CountedFailureHandler>();
        });

        await host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new CountedFailure());

        host.Services.GetRequiredService<AttemptCounter>().Count.ShouldBe(3);
    }

    [Fact]
    public async Task global_retry_policy_applies_to_a_hot_tail_pulsar_listener()
    {
        var topic = $"persistent://public/default/globalpolicy-{Guid.NewGuid():N}";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
        });

        using var listener = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic).ProcessInline().TailFromLatest();

            opts.Policies.OnException<AttemptCountingException>().RetryTimes(3);

            opts.Services.AddSingleton(new AttemptCounter { SucceedOnAttempt = 3 });
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CountedFailureHandler>();
        });

        // TailFromLatest() only sees what is published after the reader attaches.
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        await publisher.SendAsync(new CountedFailure());

        var counter = listener.Services.GetRequiredService<AttemptCounter>();
        await waitForAsync(() => counter.Count >= 3);

        counter.Count.ShouldBe(3);
    }

    /// <summary>
    /// A plain Pulsar listener with no retry-letter topic, no dead letter topic and no native redelivery
    /// gives PulsarNativeResiliencyContinuation nothing to do at all, so the failure used to be swallowed
    /// whole -- no retry, no dead letter, no message.
    /// </summary>
    [Fact]
    public async Task global_retry_policy_applies_to_a_pulsar_listener_with_no_native_resiliency()
    {
        var topic = $"persistent://public/default/globalpolicy-{Guid.NewGuid():N}";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
        });

        using var listener = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic).BufferedInMemory().WithSharedSubscriptionType();

            opts.Policies.OnException<AttemptCountingException>().RetryTimes(3);

            opts.Services.AddSingleton(new AttemptCounter { SucceedOnAttempt = 3 });
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CountedFailureHandler>();
        });

        await publisher.SendAsync(new CountedFailure());

        var counter = listener.Services.GetRequiredService<AttemptCounter>();
        await waitForAsync(() => counter.Count >= 3);

        counter.Count.ShouldBe(3);
    }

    /// <summary>
    /// The other side of the coin: where native resiliency *is* configured the Pulsar rule still claims the
    /// failure ahead of the user's global policy, and the message goes to the native dead letter topic on
    /// the first attempt rather than being retried in process.
    /// </summary>
    [Fact]
    public async Task native_resiliency_still_wins_over_a_global_retry_policy()
    {
        var topic = $"persistent://public/default/globalpolicy-{Guid.NewGuid():N}";

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
        });

        using var listener = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            // The native resiliency DSL is staged -- nothing is applied to the endpoint until the
            // retry-letter stage is terminated, so DisableRetryLetterQueueing() is what commits the DLQ.
            opts.ListenToPulsarTopic(topic).BufferedInMemory().WithSharedSubscriptionType()
                .DeadLetterQueueing(DeadLetterTopic.DefaultNative)
                .DisableRetryLetterQueueing();

            opts.Policies.OnException<AttemptCountingException>().RetryTimes(3);

            opts.Services.AddSingleton(new AttemptCounter { SucceedOnAttempt = int.MaxValue });
            opts.Discovery.DisableConventionalDiscovery().IncludeType<CountedFailureHandler>();
        });

        await publisher.SendAsync(new CountedFailure());

        var counter = listener.Services.GetRequiredService<AttemptCounter>();
        await waitForAsync(() => counter.Count >= 1);

        // Give any retry that the global policy would have driven time to show up before asserting a
        // negative -- an inline RetryTimes(3) would be finished long before this.
        await Task.Delay(5.Seconds(), TestContext.Current.CancellationToken);

        counter.Count.ShouldBe(1);
    }

    private static async Task waitForAsync(Func<bool> condition, int timeoutMs = 30000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
    }
}

public class CountedFailure;

public class AttemptCountingException : Exception
{
    public AttemptCountingException(string message) : base(message)
    {
    }
}

public class AttemptCounter
{
    private int _count;

    public int SucceedOnAttempt { get; init; } = int.MaxValue;

    public int Count => _count;

    public int Increment() => Interlocked.Increment(ref _count);
}

public class CountedFailureHandler
{
    public static void Handle(CountedFailure message, AttemptCounter counter)
    {
        var attempt = counter.Increment();
        if (attempt < counter.SucceedOnAttempt)
        {
            throw new AttemptCountingException($"Simulated failure on attempt {attempt}");
        }
    }
}
