using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using Shouldly;

namespace CoreTests.Acceptance;

public class invoke_tracing_mode
{
    [Fact]
    public void default_invoke_tracing_should_be_lightweight()
    {
        var options = new WolverineOptions();
        options.InvokeTracing.ShouldBe(InvokeTracingMode.Lightweight);
    }

    [Fact]
    public async Task invoke_with_lightweight_tracing_does_not_emit_execution_log_messages()
    {
        var logger = new CapturingLogger();

        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.InvokeTracing = InvokeTracingMode.Lightweight;
                opts.Services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
            })
            .Build();

        await host.StartAsync();

        try
        {
            await host.InvokeMessageAndWaitAsync(new TracingTestMessage("hello"));

            // Lightweight mode should NOT produce execution started/finished log messages
            logger.Messages.ShouldNotContain(m =>
                m.Contains("Started processing") && m.Contains("TracingTestMessage"));
            logger.Messages.ShouldNotContain(m =>
                m.Contains("Successfully processed") && m.Contains("TracingTestMessage"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task invoke_with_full_tracing_emits_execution_log_messages()
    {
        var logger = new CapturingLogger();

        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.InvokeTracing = InvokeTracingMode.Full;
                opts.Services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
            })
            .Build();

        await host.StartAsync();

        try
        {
            await host.InvokeMessageAndWaitAsync(new TracingTestMessage("hello"));

            // Full mode SHOULD produce execution started and succeeded log messages
            logger.Messages.ShouldContain(m =>
                m.Contains("Started processing") && m.Contains("TracingTestMessage"));
            logger.Messages.ShouldContain(m =>
                m.Contains("Successfully processed") && m.Contains("TracingTestMessage"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task invoke_with_full_tracing_emits_failure_log_on_exception()
    {
        var logger = new CapturingLogger();

        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.InvokeTracing = InvokeTracingMode.Full;
                opts.Services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
            })
            .Build();

        await host.StartAsync();

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await host.InvokeMessageAndWaitAsync(new TracingTestFailingMessage()));

            // Full mode SHOULD produce failed log message
            logger.Messages.ShouldContain(m =>
                m.Contains("Failed to process") && m.Contains("TracingTestFailingMessage"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task invoke_with_full_tracing_emits_finished_log_on_exception()
    {
        var logger = new CapturingLogger();

        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.InvokeTracing = InvokeTracingMode.Full;
                opts.Services.AddSingleton<ILoggerFactory>(new SingleLoggerFactory(logger));
            })
            .Build();

        await host.StartAsync();

        try
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await host.InvokeMessageAndWaitAsync(new TracingTestFailingMessage()));

            // Should still log "Finished processing" even on failure
            logger.Messages.ShouldContain(m =>
                m.Contains("Finished processing") && m.Contains("TracingTestFailingMessage"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}

public record TracingTestMessage(string Text);

public static class TracingTestMessageHandler
{
    public static void Handle(TracingTestMessage message)
    {
        // Simple handler — just completes successfully
    }
}

public record TracingTestFailingMessage;

public static class TracingTestFailingMessageHandler
{
    public static void Handle(TracingTestFailingMessage message)
    {
        throw new InvalidOperationException("Deliberate test failure");
    }
}

/// <summary>
/// Simple logger that captures formatted messages for test assertions
/// </summary>
internal class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = new();
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        lock (Messages)
        {
            Messages.Add(message);
            Entries.Add((logLevel, message, exception));
        }
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}

/// <summary>
/// Logger factory that returns the same logger instance for all categories
/// </summary>
internal class SingleLoggerFactory : ILoggerFactory
{
    private readonly ILogger _logger;

    public SingleLoggerFactory(ILogger logger)
    {
        _logger = logger;
    }

    public ILogger CreateLogger(string categoryName) => _logger;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}
