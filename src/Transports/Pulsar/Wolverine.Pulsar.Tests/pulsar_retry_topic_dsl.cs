using System.Collections.Concurrent;
using DotPulsar;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3182: first-class, discoverable retry-letter error policy (MoveToPulsarRetryTopic) — the Pulsar
// analogue of MoveToKafkaRetryTopic. Routes a failed message through tiered retry-letter delays then to
// the dead-letter topic, and warns at startup when applied alongside non-Pulsar listeners.
[Collection("pulsar")]
public class pulsar_retry_topic_dsl
{
    [Fact]
    public async Task permanent_failure_walks_the_tiers_then_dead_letters()
    {
        var topicPath = $"persistent://public/default/retry-dsl-{Guid.NewGuid():N}";

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishMessage<RetryDslMessage>().ToPulsarTopic(topicPath);

            opts.ListenToPulsarTopic(topicPath)
                .SubscriptionType(SubscriptionType.Shared)
                .ProcessInline();

            // The retry-letter policy under test: two tiers, then the dead-letter topic.
            opts.OnException<RetryDslFailure>().MoveToPulsarRetryTopic(2.Seconds(), 3.Seconds());

            opts.Services.AddSingleton<RetryDslSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<RetryDslHandler>();
        });

        var session = await host.TrackActivity(60.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .IncludeExternalTransports()
            .WaitForCondition(new WaitForDeadLetteredMessage<RetryDslMessage>())
            .SendMessageAndWaitAsync(new RetryDslMessage());

        // Tried on the source + both retry tiers (initial + 2 tiers = 3 deliveries), then dead-lettered.
        session.Received.MessagesOf<RetryDslMessage>().Count().ShouldBe(3);
        session.MovedToErrorQueue.MessagesOf<RetryDslMessage>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task non_pulsar_listener_emits_a_startup_warning()
    {
        var topicPath = $"persistent://public/default/retry-dsl-warn-{Guid.NewGuid():N}";
        var capture = new CapturingLoggerProvider();

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));

            opts.ListenToPulsarTopic(topicPath)
                .SubscriptionType(SubscriptionType.Shared)
                .ProcessInline();

            // A non-Pulsar listener present alongside the policy -> should warn at startup.
            opts.ListenAtPort(PortFinder.GetAvailablePort());

            opts.OnException<RetryDslFailure>().MoveToPulsarRetryTopic(2.Seconds(), 3.Seconds());

            opts.Services.AddSingleton<ILoggerProvider>(capture);
            opts.Discovery.DisableConventionalDiscovery();
        });

        capture.Warnings.ShouldContain(w =>
            w.Contains("MoveToPulsarRetryTopic") && w.Contains("non-Pulsar"));
    }
}

public class RetryDslMessage;

public class RetryDslFailure : Exception
{
    public RetryDslFailure() : base("simulated retry-dsl failure")
    {
    }
}

public class RetryDslSink
{
    public ConcurrentBag<DateTimeOffset> Deliveries { get; } = new();
}

public class RetryDslHandler
{
    public void Handle(RetryDslMessage message, RetryDslSink sink)
    {
        sink.Deliveries.Add(DateTimeOffset.UtcNow);
        throw new RetryDslFailure();
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<string> Warnings { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentBag<string> warnings) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
