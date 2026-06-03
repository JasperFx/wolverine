using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.Blocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class buffered_receiver_null_listener_guard_3013
{
    [Fact]
    public async Task listenerless_envelope_does_not_NRE_in_defer_or_complete_blocks()
    {
        // GH-3013: the empty all-zero startup / agent-handshake envelope reaches the buffered
        // receiver's defer/complete retry blocks with Listener == null. Pre-fix the unguarded
        // env.Listener! deref NRE'd inside the retry loop; RetryBlock catches + logs it (and retries),
        // so the failure surfaces only as logged errors that aggregate into tracked-session failures
        // across the consumer's whole integration sweep — not as a thrown exception here.
        var captor = new CapturingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(captor))
            .UseWolverine(opts => { opts.LocalQueue("buffered-3013"); })
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var endpoint = (LocalQueue?)runtime.Endpoints.EndpointByName("buffered-3013")
                       ?? throw new InvalidOperationException("buffered-3013 not found");
        var receiver = (BufferedReceiver)endpoint.Agent!;

        captor.Errors.Clear(); // ignore anything logged during bootstrap

        var deferBlock = retryBlock(receiver, "_deferBlock");
        var completeBlock = retryBlock(receiver, "_completeBlock");

        // The malformed system envelope: no Listener, no Destination.
        var empty = new Envelope();

        await deferBlock.PostAsync(empty);
        await completeBlock.PostAsync(empty);
        await deferBlock.DrainAsync();
        await completeBlock.DrainAsync();

        captor.Errors.ShouldNotContain(e => e is NullReferenceException);
    }

    private static RetryBlock<Envelope> retryBlock(BufferedReceiver receiver, string field)
    {
        var info = typeof(BufferedReceiver).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (RetryBlock<Envelope>)info.GetValue(receiver)!;
    }
}

/// <summary>
/// Minimal <see cref="ILoggerProvider"/> that records the exceptions passed to any log call,
/// so a test can assert no <see cref="NullReferenceException"/> was logged (the GH-3013 symptom,
/// which <c>RetryBlock</c> swallows and logs rather than re-throwing).
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<Exception> Errors { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Errors);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentBag<Exception> errors) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception != null)
            {
                errors.Add(exception);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
