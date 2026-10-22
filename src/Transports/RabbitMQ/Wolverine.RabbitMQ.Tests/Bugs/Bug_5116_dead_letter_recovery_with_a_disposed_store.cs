using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5116. The dead letter recovery listener acks a dead letter as soon as the message store's
/// MoveToDeadLetterStorageAsync returns, and a disposed store used to return without writing anything. A
/// host that disposed its store while the listener was still draining, such as one that failed to start,
/// deleted dead letters from the broker that never reached wolverine_dead_letters. The store now throws,
/// and the dead letter stays in the queue.
/// </summary>
public class Bug_5116_dead_letter_recovery_with_a_disposed_store : IAsyncLifetime
{
    private readonly string _queueName = "gh5116_" + Guid.NewGuid().ToString("N")[..8];
    private string DeadLetterQueueName => _queueName + "_DLQ";
    private readonly RecoveryFailures _failures = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Gh5116";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.EnableAutomaticFailureAcks = false;

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gh5116_rabbit");

                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLetterQueueRecovery(DeadLetterQueueName);

                opts.PublishMessage<Gh5116Message>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName)
                    .DeadLetterQueueing(new DeadLetterQueue(DeadLetterQueueName));

                opts.LocalRoutingConventionDisabled = true;

                opts.Services.AddSingleton<ILoggerProvider>(_failures);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null!)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        // Not TeardownResources(), which would need the store this test disposed
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        await channel.QueueDeleteAsync(_queueName);
        await channel.QueueDeleteAsync(DeadLetterQueueName);
    }

    [Fact]
    public async Task a_dead_letter_the_disposed_store_could_not_write_stays_in_the_dead_letter_queue()
    {
        // What a host that failed to start does to the store while the recovery listener is still draining
        await _host.GetRuntime().Storage.DisposeAsync();

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new Gh5116Message());

        await _failures.WaitForAsync<DeadLetterQueueListener>();

        // Stopping closes the listener's channel, so anything it left unacked is back in the queue
        var token = TestContext.Current.CancellationToken;
        await _host.StopAsync(token);

        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync(token);
        await using var channel = await conn.CreateChannelAsync(cancellationToken: token);
        (await channel.QueueDeclarePassiveAsync(DeadLetterQueueName, token)).MessageCount.ShouldBe(1u);
    }
}

public record Gh5116Message;

public static class Gh5116MessageHandler
{
    public static void Handle(Gh5116Message message) => throw new DivideByZeroException("GH-5116");
}

/// <summary>
/// Records the errors a dead letter recovery listener logs, so a test can wait for its attempt to recover a
/// message to have been made and failed
/// </summary>
public class RecoveryFailures : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, Exception? Exception)> _errors = new();

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, _errors);

    public async Task WaitForAsync<TListener>()
    {
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_errors.Any(x => x.Category == typeof(TListener).FullName && x.Exception is ObjectDisposedException)) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException($"{typeof(TListener).Name} never failed to recover a dead letter into the disposed store");
    }

    public void Dispose()
    {
    }

    private class Recorder(string category, ConcurrentQueue<(string, Exception?)> errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) errors.Enqueue((category, exception));
        }
    }
}
