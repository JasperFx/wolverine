using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5809. A requeue or native scheduled retry from a subscription listener sends its copy to the service's retry
/// queue, which only exists while system queues are enabled. Without it the copy is published to the TOPIC, so every
/// subscription on that topic receives it again. Wolverine says so at startup, and
/// <see cref="AzureServiceBusConfiguration.AssertSubscriptionRequeuesDoNotFanOut"/> refuses to start instead.
/// External transports are stubbed: the listener validation runs anyway, so none of this needs a broker.
/// </summary>
public class subscription_requeue_without_system_queues_5809
{
    private static AzureServiceBusSubscription listeningSubscription(Action<AzureServiceBusTransport> configure)
    {
        var transport = new AzureServiceBusTransport();
        configure(transport);

        var topic = transport.Topics["topic5809"];
        var subscription = new AzureServiceBusSubscription(transport, topic, "subscription5809");
        transport.Subscriptions.Add(subscription);
        subscription.IsListener = true;

        return subscription;
    }

    [Fact]
    public void no_problem_while_system_queues_are_enabled()
    {
        var subscription = listeningSubscription(_ => { });

        ListenerConfigurationValidator.Validate(subscription).ShouldBeEmpty();
    }

    [Fact]
    public void a_warning_when_system_queues_are_disabled()
    {
        var subscription = listeningSubscription(x => x.SystemQueuesEnabled = false);

        var problem = ListenerConfigurationValidator.Validate(subscription).ShouldHaveSingleItem();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
        problem.Message.ShouldContain(subscription.Uri.ToString());
        problem.Message.ShouldContain("every subscription");
        problem.Message.ShouldContain("SystemQueuesAreEnabled(true)");
    }

    [Fact]
    public void fatal_when_system_queues_are_disabled_and_the_assertion_is_on()
    {
        var subscription = listeningSubscription(x =>
        {
            x.SystemQueuesEnabled = false;
            x.AssertsSubscriptionRequeuesDoNotFanOut = true;
        });

        var problem = ListenerConfigurationValidator.Validate(subscription).ShouldHaveSingleItem();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Fatal);
        problem.Message.ShouldContain(subscription.Uri.ToString());
    }

    [Fact]
    public void the_assertion_is_quiet_while_system_queues_are_enabled()
    {
        var subscription = listeningSubscription(x => x.AssertsSubscriptionRequeuesDoNotFanOut = true);

        ListenerConfigurationValidator.Validate(subscription).ShouldBeEmpty();
    }

    [Fact]
    public void nothing_for_a_subscription_that_is_not_a_listener()
    {
        var subscription = listeningSubscription(x => x.SystemQueuesEnabled = false);
        subscription.IsListener = false;

        ListenerConfigurationValidator.Validate(subscription).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_warning_is_logged_at_startup()
    {
        var logs = new WarningCapturingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.StubAllExternalTransports();
                opts.UseAzureServiceBus(Servers.AzureServiceBusConnectionString)
                    .SystemQueuesAreEnabled(false);

                opts.ListenToAzureServiceBusSubscription("warned5809").FromTopic("topic5809");
            }).StartAsync(TestContext.Current.CancellationToken);

        var warning = logs.Warnings.ShouldHaveSingleItem(
            "Expected exactly one subscription requeue warning");
        warning.ShouldContain("warned5809");
        warning.ShouldContain("every subscription");
    }

    [Fact]
    public async Task no_warning_at_startup_while_system_queues_are_enabled()
    {
        var logs = new WarningCapturingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.StubAllExternalTransports();
                opts.UseAzureServiceBus(Servers.AzureServiceBusConnectionString);

                opts.ListenToAzureServiceBusSubscription("quiet5809").FromTopic("topic5809");
            }).StartAsync(TestContext.Current.CancellationToken);

        logs.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_host_refuses_to_start_with_the_assertion()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
            {
                opts.StubAllExternalTransports();
                opts.UseAzureServiceBus(Servers.AzureServiceBusConnectionString)
                    .SystemQueuesAreEnabled(false)
                    .AssertSubscriptionRequeuesDoNotFanOut();

                opts.ListenToAzureServiceBusSubscription("refused5809").FromTopic("topic5809");
            }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("refused5809");
        ex.Message.ShouldContain("SystemQueuesAreEnabled(true)");
    }

    /// <summary>
    /// Only the GH-5809 message is kept, so unrelated startup warnings cannot make these tests brittle
    /// </summary>
    private class WarningCapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new WarningCapturingLogger(Warnings);

        public void Dispose()
        {
        }

        private class WarningCapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning) return;

                var message = formatter(state, exception);
                if (!message.Contains("fan", StringComparison.OrdinalIgnoreCase)) return;

                lock (warnings)
                {
                    warnings.Add(message);
                }
            }
        }
    }
}
