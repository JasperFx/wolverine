using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5946. <c>RabbitMqHealthCheck.GetBrokerQueueDepthAsync</c> answered null for a queue the management page showed
/// 75+ messages in, and every way it could get there was silent: a sending connection that existed but was down was
/// never swapped for the listening one, every exception was swallowed, and only the default <c>rabbitmq</c> scheme
/// was recognized, so an endpoint on a named broker always got null.
/// </summary>
public class broker_queue_depth_5946
{
    private readonly BrokerName theBrokerName = new("depth5946");

    private static async Task<IHost> publisherAsync(Action<WolverineOptions> configure, CapturingLoggerProvider? logs = null)
    {
        return await WolverineHost.ForAsync(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            configure(opts);
            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

            if (logs != null)
            {
                opts.Services.AddSingleton<ILoggerProvider>(logs);
            }
        });
    }

    private static async Task sendAsync(IHost host, int count)
    {
        var bus = host.MessageBus();
        for (var i = 0; i < count; i++)
        {
            await bus.SendAsync(new DepthMessage(i));
        }
    }

    // Nothing listens to the queue, so whatever is sent stays there
    private static async Task<long?> waitForDepthAsync(WolverineTransportHealthCheck healthCheck, Uri uri, long expected)
    {
        long? depth = null;
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            depth = await healthCheck.GetBrokerQueueDepthAsync(uri, TestContext.Current.CancellationToken);
            if (depth == expected) return depth;
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        return depth;
    }

    [Fact]
    public async Task reports_the_depth_of_a_queue_on_the_default_broker()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var host = await publisherAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            opts.PublishMessage<DepthMessage>().ToRabbitQueue(queueName).SendInline();
        });

        await sendAsync(host, 3);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();
        var healthCheck = transport.BuildHealthCheck(host.GetRuntime());

        (await waitForDepthAsync(healthCheck, new Uri($"rabbitmq://queue/{queueName}"), 3)).ShouldBe(3);
    }

    [Fact]
    public async Task falls_back_to_the_listening_connection_when_the_sending_one_is_down()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var host = await publisherAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
            opts.PublishMessage<DepthMessage>().ToRabbitQueue(queueName).SendInline();
        });

        await sendAsync(host, 3);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();
        var healthCheck = transport.BuildHealthCheck(host.GetRuntime());
        var uri = new Uri($"rabbitmq://queue/{queueName}");
        (await waitForDepthAsync(healthCheck, uri, 3)).ShouldBe(3);

        // The sending connection exists, but is between a drop and its recovery
        var sending = transport.TryGetSendingConnection()!;
        sending.IsConnected = false;
        try
        {
            transport.TryGetListeningConnection()!.IsConnected.ShouldBeTrue();
            (await healthCheck.GetBrokerQueueDepthAsync(uri, TestContext.Current.CancellationToken)).ShouldBe(3);
        }
        finally
        {
            sending.IsConnected = true;
        }
    }

    [Fact]
    public async Task reports_the_depth_of_a_queue_on_a_named_broker()
    {
        var queueName = RabbitTesting.NextQueueName();
        using var host = await publisherAsync(opts =>
        {
            opts.AddNamedRabbitMqBroker(theBrokerName, _ => { }).AutoProvision().AutoPurgeOnStartup();
            opts.PublishMessage<DepthMessage>().ToRabbitQueueOnNamedBroker(theBrokerName, queueName).SendInline();
        });

        await sendAsync(host, 3);

        var transport = (RabbitMqTransport)host.GetRuntime().Options.Transports.ForScheme(theBrokerName.Name)!;
        var healthCheck = transport.BuildHealthCheck(host.GetRuntime());

        healthCheck.Protocol.ShouldBe(theBrokerName.Name);
        (await waitForDepthAsync(healthCheck, new Uri($"{theBrokerName.Name}://queue/{queueName}"), 3)).ShouldBe(3);
    }

    [Fact]
    public async Task a_failed_query_is_logged_with_its_exception()
    {
        var logs = new CapturingLoggerProvider();
        using var host = await publisherAsync(opts => opts.UseRabbitMq().AutoProvision(), logs);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<RabbitMqTransport>();
        var healthCheck = transport.BuildHealthCheck(host.GetRuntime());

        // A queue the broker does not have
        var uri = new Uri($"rabbitmq://queue/{RabbitTesting.NextQueueName()}-missing");
        (await healthCheck.GetBrokerQueueDepthAsync(uri, TestContext.Current.CancellationToken)).ShouldBeNull();

        var entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Exception.ShouldNotBeNull();
        entry.Message.ShouldContain(uri.ToString());
    }
}

public record DepthMessage(int Number);

/// <summary>Captures what the health check logs through the transport's logger.</summary>
public class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) =>
        categoryName == typeof(RabbitMqHealthCheck).FullName ? new CapturingLogger(this) : NullLoggerInstance;

    private static readonly ILogger NullLoggerInstance = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private class CapturingLogger(CapturingLoggerProvider parent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            parent.Entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }
}
