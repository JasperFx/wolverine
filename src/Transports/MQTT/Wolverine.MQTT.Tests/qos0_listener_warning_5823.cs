using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Protocol;
using Shouldly;

namespace Wolverine.MQTT.Tests;

/// <summary>
///     GH-5823. A requeued or deferred MQTT message is kept unacknowledged so the broker can resend it (GH-5770), but
///     at QoS 0 the broker never resends anything, so a message requeued, deferred or in flight at shutdown is lost.
///     That is MQTT's at-most-once contract, and Wolverine says so once per QoS 0 listener at startup.
/// </summary>
/// <remarks>
///     The check runs over the compiled listener configuration, which Wolverine validates even when the external
///     transports are stubbed, so none of these need a broker.
/// </remarks>
public class qos0_listener_warning_5823
{
    private static async Task<RecordingLoggerProvider> startAsync(Action<WolverineOptions> configure)
    {
        var recorder = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(recorder))
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt => mqtt.WithClientOptions(client => client.WithTcpServer("localhost")));
                configure(opts);
                opts.StubAllExternalTransports();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);

        return recorder;
    }

    private static string[] qos0Warnings(RecordingLoggerProvider recorder)
    {
        return recorder.Warnings.Where(x => x.Contains("QoS 0 (AtMostOnce)")).ToArray();
    }

    [Fact]
    public async Task a_qos0_listener_warns_that_requeue_defer_and_shutdown_can_lose_its_messages()
    {
        var recorder = await startAsync(opts =>
        {
            opts.ListenToMqttTopic("qos0/5823").QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
        });

        var warning = qos0Warnings(recorder).ShouldHaveSingleItem();
        warning.ShouldContain("topic 'qos0/5823'");
        warning.ShouldContain("requeued or deferred");
        warning.ShouldContain("shuts down");
    }

    [Fact]
    public async Task a_qos1_listener_does_not_warn()
    {
        var recorder = await startAsync(opts =>
        {
            opts.ListenToMqttTopic("qos1/5823").QualityOfService(MqttQualityOfServiceLevel.AtLeastOnce);
        });

        qos0Warnings(recorder).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_listener_at_the_default_qos_does_not_warn()
    {
        var recorder = await startAsync(opts => opts.ListenToMqttTopic("default/5823"));

        qos0Warnings(recorder).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_qos0_topic_that_is_only_published_to_does_not_warn()
    {
        var recorder = await startAsync(opts =>
        {
            opts.PublishAllMessages().ToMqttTopic("publishonly/5823").QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
        });

        qos0Warnings(recorder).ShouldBeEmpty();
    }

    private class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private class RecordingLogger(RecordingLoggerProvider parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    parent.Warnings.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
