using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-5043. Every failed attempt was logged at Error, even one an error handling rule then retried and that
// succeeded next time
public class failure_log_level
{
    private static async Task<(IHost host, CapturingLoggerProvider logs)> startAsync(Action<WolverineOptions> configure)
    {
        var logs = new CapturingLoggerProvider();
        var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FlakyMessageHandler))
                    .IncludeType(typeof(QuietlyFlakyMessageHandler))
                    .IncludeType(typeof(RequeuedOnceMessageHandler));

                opts.OnException<FlakyException>().RetryTimes(2);
                opts.OnException<RequeueOnceException>().Requeue();
                configure(opts);
            }).StartAsync();

        return (host, logs);
    }

    private static HandlerChain chainFor<T>(IHost host)
    {
        return host.GetRuntime().Handlers.HandlerFor<T>()!.As<MessageHandler>().Chain!;
    }

    [Fact]
    public async Task failures_log_at_error_by_default()
    {
        var (host, logs) = await startAsync(_ => { });
        using (host)
        {
            chainFor<FlakyMessage>(host).FailureLogLevel.ShouldBe(LogLevel.Error);

            FlakyMessageHandler.Attempts = 0;
            await host.SendMessageAndWaitAsync(new FlakyMessage());

            logs.FailureLevels().ShouldBe([LogLevel.Error]);
        }
    }

    [Fact]
    public async Task policy_sets_the_failure_log_level_for_every_chain()
    {
        var (host, logs) = await startAsync(opts => opts.Policies.MessageFailureLogLevel(LogLevel.Warning));
        using (host)
        {
            chainFor<FlakyMessage>(host).FailureLogLevel.ShouldBe(LogLevel.Warning);

            FlakyMessageHandler.Attempts = 0;
            await host.SendMessageAndWaitAsync(new FlakyMessage());

            // The first attempt failed and was retried
            FlakyMessageHandler.Attempts.ShouldBe(2);
            logs.FailureLevels().ShouldBe([LogLevel.Warning]);
        }
    }

    [Fact]
    public async Task attribute_sets_the_failure_log_level_for_one_chain()
    {
        var (host, _) = await startAsync(_ => { });
        using (host)
        {
            chainFor<QuietlyFlakyMessage>(host).FailureLogLevel.ShouldBe(LogLevel.Information);
            chainFor<FlakyMessage>(host).FailureLogLevel.ShouldBe(LogLevel.Error);
        }
    }

    [Fact]
    public async Task a_requeue_logs_at_error_by_default()
    {
        var (host, logs) = await startAsync(_ => { });
        using (host)
        {
            RequeuedOnceMessageHandler.Attempts = 0;
            await host.SendMessageAndWaitAsync(new RequeuedOnceMessage());
            RequeuedOnceMessageHandler.Attempts.ShouldBe(2);
            logs.RescheduledLevels().ShouldBe([LogLevel.Error]);
        }
    }

    [Fact]
    public async Task a_requeue_logs_at_the_failure_log_level_of_the_chain()
    {
        // The requeue is the retry of the failed attempt, so it must not log above the attempt itself
        var (host, logs) = await startAsync(opts => opts.Policies.MessageFailureLogLevel(LogLevel.Warning));
        using (host)
        {
            RequeuedOnceMessageHandler.Attempts = 0;
            await host.SendMessageAndWaitAsync(new RequeuedOnceMessage());
            RequeuedOnceMessageHandler.Attempts.ShouldBe(2);
            logs.FailureLevels().ShouldBe([LogLevel.Warning]);
            logs.RescheduledLevels().ShouldBe([LogLevel.Warning]);
        }
    }

    // GH-5661. A message type handled by two handler types has two handler chains, one per handler type. There is
    // no single handler for the message type to ask for its level, so each requeue logs at the level of the handler
    // that actually failed
    private static async Task<(IHost host, CapturingLoggerProvider logs)> startSeparatedAsync(Action<WolverineOptions> configure)
    {
        var logs = new CapturingLoggerProvider();
        var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(LoudSeparatedHandler))
                    .IncludeType(typeof(QuietSeparatedHandler));

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                configure(opts);
            }).StartAsync();

        return (host, logs);
    }

    [Fact]
    public async Task a_requeue_of_a_message_with_two_handler_types_logs_at_the_level_of_the_handler_that_failed()
    {
        var (host, logs) = await startSeparatedAsync(opts => opts.OnException<RequeueOnceException>().Requeue());
        using (host)
        {
            host.GetRuntime().Handlers.ChainFor<SeparatedFlakyMessage>()!.ByEndpoint.Count.ShouldBe(2);

            LoudSeparatedHandler.Attempts = 0;
            QuietSeparatedHandler.Attempts = 0;
            await host.SendMessageAndWaitAsync(new SeparatedFlakyMessage());

            LoudSeparatedHandler.Attempts.ShouldBe(2);
            QuietSeparatedHandler.Attempts.ShouldBe(2);
            logs.RescheduledLevels().OrderBy(x => x).ShouldBe([LogLevel.Information, LogLevel.Error]);
        }
    }

    [Fact]
    public async Task a_scheduled_retry_of_a_message_with_two_handler_types_logs_at_the_failure_log_level()
    {
        var (host, logs) = await startSeparatedAsync(opts =>
        {
            opts.OnException<RequeueOnceException>().ScheduleRetry(100.Milliseconds());
            opts.Policies.MessageFailureLogLevel(LogLevel.Warning);
        });

        using (host)
        {
            LoudSeparatedHandler.Attempts = 0;
            QuietSeparatedHandler.Attempts = 0;
            await host.SendMessageAndWaitAsync(new SeparatedFlakyMessage());

            // The scheduled retries run after the tracked session is done
            var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
            while ((LoudSeparatedHandler.Attempts < 2 || QuietSeparatedHandler.Attempts < 2) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50.Milliseconds(), TestContext.Current.CancellationToken);
            }

            LoudSeparatedHandler.Attempts.ShouldBe(2);
            QuietSeparatedHandler.Attempts.ShouldBe(2);

            // The attribute on QuietSeparatedHandler is applied after the policy
            logs.RescheduledLevels().OrderBy(x => x).ShouldBe([LogLevel.Information, LogLevel.Warning]);
        }
    }
}

public class FlakyException : Exception;

public record SeparatedFlakyMessage;

public class LoudSeparatedHandler
{
    public static int Attempts;

    public static void Handle(SeparatedFlakyMessage message)
    {
        if (Interlocked.Increment(ref Attempts) == 1) throw new RequeueOnceException();
    }
}

public class QuietSeparatedHandler
{
    public static int Attempts;

    [WolverineLogging(FailureLogLevel = LogLevel.Information)]
    public static void Handle(SeparatedFlakyMessage message)
    {
        if (Interlocked.Increment(ref Attempts) == 1) throw new RequeueOnceException();
    }
}

public class RequeueOnceException : Exception;

public record RequeuedOnceMessage;

public static class RequeuedOnceMessageHandler
{
    public static int Attempts;

    public static void Handle(RequeuedOnceMessage message)
    {
        if (Interlocked.Increment(ref Attempts) == 1) throw new RequeueOnceException();
    }
}

public record FlakyMessage;

public static class FlakyMessageHandler
{
    public static int Attempts;

    public static void Handle(FlakyMessage message)
    {
        if (Interlocked.Increment(ref Attempts) == 1) throw new FlakyException();
    }
}

public record QuietlyFlakyMessage;

public static class QuietlyFlakyMessageHandler
{
    [WolverineLogging(FailureLogLevel = LogLevel.Information)]
    public static void Handle(QuietlyFlakyMessage message)
    {
    }
}

public class CapturingLoggerProvider : ILoggerProvider
{
    public readonly ConcurrentQueue<(LogLevel Level, string Message)> Entries = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    public LogLevel[] RescheduledLevels()
    {
        return Entries.Where(x => x.Message.EndsWith("was rescheduled to queue")).Select(x => x.Level).ToArray();
    }

    public LogLevel[] FailureLevels()
    {
        return Entries.Where(x => x.Message.StartsWith("Failed to process message")).Select(x => x.Level).ToArray();
    }

    private class CapturingLogger(CapturingLoggerProvider parent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            parent.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
