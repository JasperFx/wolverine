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
                    .IncludeType(typeof(QuietlyFlakyMessageHandler));

                opts.OnException<FlakyException>().RetryTimes(2);
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
}

public class FlakyException : Exception;

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
