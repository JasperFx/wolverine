using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.Runtime;

// GH-5254. Shutdown drained a buffered local queue without latching it first, and BufferedReceiver.DrainAsync()
// only waits for in-flight messages when the receiver is already latched. So IHost.StopAsync() returned while a
// handler was still running, the host disposed its logging under it, and the handler's error handling then
// logged to a disposed provider -- on Windows, the EventLog provider's ObjectDisposedException, followed by the
// "could not recover from a cascading failure" error from the recovery path. The handler here is held at a gate
// until the test lets it go, then fails and is moved to the error queue, exactly like metrics_source_tag's
// MetricFail.
public class buffered_local_queue_shutdown_waits_for_in_flight_5254
{
    [Fact]
    public async Task stopping_the_host_waits_for_a_handler_still_running_on_a_buffered_local_queue()
    {
        var logs = new DisposalAwareLoggerProvider();
        var gate = new GatedFailure5254();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.OnException<InvalidOperationException>().MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<GatedFailure5254Handler>();
                opts.Services.AddSingleton(gate);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.MessageBus().PublishAsync(new GatedFailure5254Message());
        await gate.Entered.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var stopping = host.StopAsync(TestContext.Current.CancellationToken);

        // The handler is still running on the local queue, so shutdown must not get past it
        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        stopping.IsCompleted.ShouldBeFalse();

        gate.Release();
        await stopping.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The failure was handled, and logged, before shutdown finished
        logs.Messages.ShouldContain(x => x.Contains("error queue", StringComparison.OrdinalIgnoreCase));

        host.Dispose();
        logs.LoggedAfterDisposal.ShouldBeEmpty();
    }
}

public record GatedFailure5254Message;

public class GatedFailure5254
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _release.TrySetResult();

    public async Task WaitAsync()
    {
        Entered.TrySetResult();
        await _release.Task;
    }
}

public class GatedFailure5254Handler
{
    public static async Task Handle(GatedFailure5254Message _, GatedFailure5254 gate)
    {
        await gate.WaitAsync();
        throw new InvalidOperationException("boom");
    }
}

// Stands in for a provider like EventLog that cannot be written to once the host has disposed it
public class DisposalAwareLoggerProvider : ILoggerProvider, ILogger
{
    private volatile bool _disposed;
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<string> _loggedAfterDisposal = new();

    public IReadOnlyList<string> Messages => _messages.ToArray();
    public IReadOnlyList<string> LoggedAfterDisposal => _loggedAfterDisposal.ToArray();

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        (_disposed ? _loggedAfterDisposal : _messages).Enqueue(message);
    }

    public void Dispose() => _disposed = true;
}
