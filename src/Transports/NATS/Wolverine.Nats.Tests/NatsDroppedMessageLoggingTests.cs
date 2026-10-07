using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A core NATS subscription buffers incoming messages in a bounded channel (1,024 messages by default), and NATS.Net
/// drops the newest message once that channel is full. Core NATS has no redelivery, so the message is gone; NATS.Net
/// only reports it through connection events. Wolverine has to make that loss visible in its own logs.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsDroppedMessageLoggingTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsDroppedMessageLoggingTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task dropped_core_message_is_logged()
    {
        var subject = $"drops.{Guid.NewGuid():N}";
        var logs = new WarningRecorder();
        ParkedCoreMessageHandler.Reset();

        using var receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output).AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DropsReceiver";
                opts.UseNats(_fixture.ConnectionString);

                // Inline, so a handler that does not return holds up the subscription
                opts.ListenToNatsSubject(subject).ProcessInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DropsSender";
                opts.UseNats(_fixture.ConnectionString);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<ParkedCoreMessage>().ToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        try
        {
            // The first message parks the handler, the next 1,024 fill the pending channel, the rest are dropped
            var bus = sender.MessageBus();
            for (var i = 0; i < 1_100; i++)
            {
                await bus.PublishAsync(new ParkedCoreMessage(i));
            }

            var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
            while (DateTimeOffset.UtcNow < deadline && !logs.Contains("dropped a message", subject))
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            _output.WriteLine($"dropped-message warnings: {logs.Count("dropped a message", subject)}");
            logs.Contains("dropped a message", subject).ShouldBeTrue();
            logs.Contains("slow consumer", subject).ShouldBeTrue();
        }
        finally
        {
            ParkedCoreMessageHandler.Release();
        }
    }

    private class WarningRecorder : ILoggerProvider, ILogger
    {
        private readonly List<string> _warnings = new();

        public bool Contains(string text, string subject) => Count(text, subject) > 0;

        public int Count(string text, string subject)
        {
            lock (_warnings)
            {
                return _warnings.Count(x => x.Contains(text) && x.Contains(subject));
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;

            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}

public record ParkedCoreMessage(int Number);

public static class ParkedCoreMessageHandler
{
    private static TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Release()
    {
        _gate.TrySetResult();
    }

    public static Task Handle(ParkedCoreMessage message) => _gate.Task;
}
