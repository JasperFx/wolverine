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
        var window = 1.Seconds();

        IHost? publisher = null;
        IHost? receiver = null;

        try
        {
            publisher = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                    opts.PublishAllMessages().ToRabbitQueue(queueName);
                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            receiver = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                opts.ApplicationAssembly = typeof(rate_limiting_end_to_end).Assembly;
                    opts.Services.AddSingleton(tracker);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
                    opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
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

            (second - first).ShouldBeGreaterThanOrEqualTo(700.Milliseconds());
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
        var queueName = $"rate-limit-pause-{Guid.NewGuid():N}";
        var schemaName = $"rate_limit_pause_{Guid.NewGuid():N}";
        var window = 5.Seconds();
        var exceptions = new List<Exception>();
        var logs = new List<string>();

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

                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
                    opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                    opts.ListenToRabbitQueue(queueName).UseDurableInbox();

                    opts.Policies.ForMessagesOfType<RateLimitedPauseMessage>()
                        .RateLimit("pause-test", new RateLimit(1, window));

                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            publisher = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision();
                    opts.PublishAllMessages().ToRabbitQueue(queueName);
                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            await publisher.ResetResourceState();
            await receiver.ResetResourceState();
            await Task.Delay(500.Milliseconds());

            var bus = publisher.MessageBus();
            for (var i = 0; i < 10; i++)
            {
                await bus.PublishAsync(new RateLimitedPauseMessage());
            }

            // Wait long enough for rescheduling to occur
            await Task.Delay(8.Seconds());

            // The critical assertion: no NullReferenceException during pause/resume
            exceptions.Any(ContainsNullRef).ShouldBeFalse(
                "Expected no NullReferenceException during rate-limited pause/resume cycle");
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

    private static bool ContainsNullRef(Exception ex)
    {
        var current = ex;
        while (current != null)
        {
            if (current is NullReferenceException) return true;
            current = current.InnerException;
        }
        return false;
    }

    private static async Task alignToWindowStart(TimeSpan window)
    {
        var windowTicks = window.Ticks;
        var thresholdTicks = 50.Milliseconds().Ticks;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (DateTimeOffset.UtcNow.Ticks % windowTicks < thresholdTicks)
            {
                return;
            }

            await Task.Delay(10.Milliseconds());
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

    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _logs, _exceptions, _output);
    public void Dispose() { }
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

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var line = $"{_categoryName}/{logLevel}: {message}";
        _logs.Add(line);
        try { _output.WriteLine(line); } catch { /* disposed output */ }
        if (exception != null)
        {
            _exceptions.Add(exception);
            try { _output.WriteLine(exception.ToString()); } catch { }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
