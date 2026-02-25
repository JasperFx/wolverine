using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RateLimiting;
using Wolverine.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class rate_limiting_end_to_end
{
    private readonly ITestOutputHelper _output;

    public rate_limiting_end_to_end(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task rate_limited_messages_are_delayed_over_rabbitmq()
    {
        var queueName = $"rate-limit-{Guid.NewGuid():N}";
        var schemaName = $"rate_limit_{Guid.NewGuid():N}";
        var tracker = new RateLimitTracker();
        var window = 2.Seconds();

        IHost? publisher = null;
        IHost? receiver = null;

        try
        {
            publisher = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseRabbitMq().DisableDeadLetterQueueing().UseQuorumQueues().AutoProvision().AutoPurgeOnStartup();
                    opts.PublishAllMessages().ToRabbitQueue(queueName);
                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            receiver = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                opts.ApplicationAssembly = typeof(rate_limiting_end_to_end).Assembly;
                    opts.Services.AddSingleton(tracker);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
                opts.UseRabbitMq().DisableDeadLetterQueueing().UseQuorumQueues().AutoProvision()
                    .AutoPurgeOnStartup();
                opts.ListenToRabbitQueue(queueName).UseDurableInbox().Sequential();

                    opts.Policies.ForMessagesOfType<RateLimitedMessage>()
                        .RateLimit("rabbitmq-rate-limit", new RateLimit(1, window));

                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            await publisher.ResetResourceState();
            await receiver.ResetResourceState();
            await alignToWindowStart(window);

            var bus = publisher.MessageBus();
            await bus.PublishAsync(new RateLimitedMessage());
            await bus.PublishAsync(new RateLimitedMessage());

            var first = await tracker.FirstHandled.Task.WaitAsync(10.Seconds());
            var second = await tracker.SecondHandled.Task.WaitAsync(10.Seconds());

            // Allow some jitter, but enforce meaningful delay
            (second - first).ShouldBeGreaterThanOrEqualTo(1500.Milliseconds());
        }
        finally
        {
            if (receiver != null)
            {
                await safeStopAsync(receiver);
            }

            if (publisher != null)
            {
                await safeStopAsync(publisher);
            }
        }
    }

    [Fact]
    public async Task rate_limited_messages_do_not_throw_when_rescheduled()
    {
        const string queueKey = "queue:communication-external-send-digital";
        const string queueName = "communication-external-send-digital";
        var schemaName = $"rate_limit_{Guid.NewGuid():N}";
        var window = 60.Seconds();
        var logs = new List<string>();
        var exceptions = new List<Exception>();

        IHost? publisher = null;
        IHost? receiver = null;

        try
        {
            receiver = await Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(new ListLoggerProvider(logs, exceptions, _output));
                    logging.SetMinimumLevel(LogLevel.Debug);
                })
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = typeof(rate_limiting_end_to_end).Assembly;

                    opts.Policies.UseDurableLocalQueues();
                    opts.Policies.UseDurableInboxOnAllListeners();
                    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                    opts.Policies.AutoApplyTransactions();

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schemaName)
                        .UseSqlServerRateLimiting();

                    opts.UseRabbitMq().DisableDeadLetterQueueing().UseQuorumQueues().AutoProvision().AutoPurgeOnStartup();
                    opts.ListenToRabbitQueue(queueName)
                        .UseDurableInbox();

                    opts.Policies.ForMessagesOfType<DigitalCommunicationProcessingEvent>()
                        .RateLimit(queueKey, new RateLimit(1, window));

                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            publisher = await Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddProvider(new ListLoggerProvider(logs, exceptions, _output));
                    logging.SetMinimumLevel(LogLevel.Debug);
                })
                .UseWolverine(opts =>
                {
                    // Avoid auto-provisioning from the publisher to prevent queue type conflicts.
                    opts.UseRabbitMq().DisableDeadLetterQueueing().UseQuorumQueues();
                    opts.PublishAllMessages().ToRabbitQueue(queueName);
                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            await publisher.ResetResourceState();
            await receiver.ResetResourceState();
            await Task.Delay(500.Milliseconds());

            var bus = publisher.MessageBus();
            for (var i = 0; i < 20; i++)
            {
                await bus.PublishAsync(new DigitalCommunicationProcessingEvent());
            }

            await assertLogContainsAsync(logs, "was rescheduled to queue", 30.Seconds());
            await assertNoExceptionAsync<NullReferenceException>(exceptions, 10.Seconds());
        }
        finally
        {
            if (receiver != null)
            {
                await safeStopAsync(receiver);
            }

            if (publisher != null)
            {
                await safeStopAsync(publisher);
            }
        }
    }

    private static async Task assertLogContainsAsync(List<string> logs, string text, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow <= stopAt)
        {
            if (logs.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            await Task.Delay(100.Milliseconds());
        }

        throw new Xunit.Sdk.XunitException($"Expected logs to contain '{text}', but did not find it.");
    }

    private static async Task assertLogNotContainsAsync(List<string> logs, string text, TimeSpan timeout)
    {
        var stopAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow <= stopAt)
        {
            if (logs.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)))
            {
                throw new Xunit.Sdk.XunitException($"Did not expect logs to contain '{text}', but it was found.");
            }

            await Task.Delay(100.Milliseconds());
        }
    }

    private static async Task assertNoExceptionAsync<TException>(List<Exception> exceptions, TimeSpan timeout)
        where TException : Exception
    {
        var stopAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow <= stopAt)
        {
            if (exceptions.Any(containsException<TException>))
            {
                throw new Xunit.Sdk.XunitException($"Did not expect exceptions of type {typeof(TException).Name}.");
            }

            await Task.Delay(100.Milliseconds());
        }
    }

    private static bool containsException<TException>(Exception exception) where TException : Exception
    {
        if (exception is TException)
        {
            return true;
        }

        var inner = exception.InnerException;
        while (inner != null)
        {
            if (inner is TException)
            {
                return true;
            }

            inner = inner.InnerException;
        }

        return false;
    }

    private static async Task alignToWindowStart(TimeSpan window)
    {
        var windowTicks = window.Ticks;
        var thresholdTicks = Math.Min(2.Seconds().Ticks, windowTicks / 10);
        var delay = window >= 10.Seconds() ? 100.Milliseconds() : 10.Milliseconds();
        var attempts = window >= 10.Seconds() ? 700 : 200;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (DateTimeOffset.UtcNow.Ticks % windowTicks < thresholdTicks)
            {
                return;
            }

            await Task.Delay(delay);
        }

        throw new TimeoutException("Could not align to rate limit window start.");
    }

    private static async Task safeStopAsync(IHost host)
    {
        try
        {
            await host.StopAsync();
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            host.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public record RateLimitedMessage;
public record RateLimitedPauseMessage;
public record ExternalSendPrintMessage;
public record DigitalCommunicationProcessingEvent;

public class RateLimitTracker
{
    private int _count;
    private readonly object _lock = new();

    public TaskCompletionSource<DateTimeOffset> FirstHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<DateTimeOffset> SecondHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RecordHandled()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _count++;
            if (_count == 1)
            {
                FirstHandled.TrySetResult(now);
            }
            else if (_count == 2)
            {
                SecondHandled.TrySetResult(now);
            }
        }
    }
}

public static class RateLimitedMessageHandler
{
    public static void Handle(RateLimitedMessage message, RateLimitTracker tracker)
    {
        tracker.RecordHandled();
    }
}

public static class RateLimitedPauseMessageHandler
{
    public static void Handle(RateLimitedPauseMessage message)
    {
    }
}

public class RetryableExternalException : Exception
{
    public RetryableExternalException(string message) : base(message)
    {
    }
}

public static class ExternalSendPrintMessageHandler
{
    public static void Handle(ExternalSendPrintMessage message)
    {
        throw new RetryableExternalException("Simulated external failure");
    }
}

public static class DigitalCommunicationProcessingEventHandler
{
    public static void Handle(DigitalCommunicationProcessingEvent message)
    {
    }
}

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<string> _logs;
    private readonly List<Exception> _exceptions;
    private readonly ITestOutputHelper _output;

    public ListLoggerProvider(List<string> logs, List<Exception> exceptions, ITestOutputHelper output)
    {
        _logs = logs;
        _exceptions = exceptions;
        _output = output;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ListLogger(categoryName, _logs, _exceptions, _output);
    }

    public void Dispose()
    {
    }
}

internal sealed class ListLogger : ILogger
{
    private readonly string _categoryName;
    private readonly List<string> _logs;
    private readonly List<Exception> _exceptions;
    private readonly ITestOutputHelper _output;

    public ListLogger(string categoryName, List<string> logs, List<Exception> exceptions, ITestOutputHelper output)
    {
        _categoryName = categoryName;
        _logs = logs;
        _exceptions = exceptions;
        _output = output;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel != LogLevel.None;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return NullScope.Instance;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var line = $"{_categoryName}/{logLevel}: {message}";
        _logs.Add(line);
        _output.WriteLine(line);
        if (exception != null)
        {
            var text = exception.ToString();
            _logs.Add(text);
            _output.WriteLine(text);
            _exceptions.Add(exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
