using System.Reflection;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5314. With EnableDeadLetterQueueRecovery(), stopping the host threw AlreadyClosedException out of
/// DeadLetterQueueListener.StopAsync. WebApplicationFactory and RunJasperFxCommands both call Host.StopAsync
/// on the same host at the same time, and the second pass closed the listener's connection underneath the
/// first pass's channel close. Stopping the listener is now idempotent, safe to run concurrently, and
/// tolerates a channel or connection that is already closed.
/// </summary>
public class Bug_5314_dead_letter_listener_stop : IAsyncLifetime
{
    private readonly string _queueName = "gh5314_" + Guid.NewGuid().ToString("N")[..8];
    private string DeadLetterQueueName => _queueName + "_DLQ";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Gh5314";

                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLetterQueueRecovery(DeadLetterQueueName);

                opts.ListenToRabbitQueue(_queueName)
                    .DeadLetterQueueing(new DeadLetterQueue(DeadLetterQueueName));

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();

        await waitForTheListenerToConsumeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();

        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        await channel.QueueDeleteAsync(_queueName);
        await channel.QueueDeleteAsync(DeadLetterQueueName);
    }

    [Fact]
    public async Task stopping_the_host_twice_at_the_same_time_does_not_throw()
    {
        var token = TestContext.Current.CancellationToken;

        await Should.NotThrowAsync(() => Task.WhenAll(_host.StopAsync(token), _host.StopAsync(token)));

        // And a later, third stop is a no-op
        await Should.NotThrowAsync(() => _host.StopAsync(token));
    }

    [Fact]
    public async Task stopping_the_listener_concurrently_does_not_throw()
    {
        var listener = theListener();
        var token = TestContext.Current.CancellationToken;

        // Started back to back, the second stop begins while the first one's channel close is in flight
        var stops = Enumerable.Range(0, 10).Select(_ => listener.StopAsync(token)).ToArray();
        await Should.NotThrowAsync(() => Task.WhenAll(stops));

        await Should.NotThrowAsync(() => _host.StopAsync(token));
    }

    [Fact]
    public async Task stopping_after_the_listener_connection_was_closed_does_not_throw()
    {
        // What the first of two concurrent stops did to the second one's channel close
        var connection = (IConnection)typeof(DeadLetterQueueListener)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(theListener())!;

        var token = TestContext.Current.CancellationToken;
        var closing = connection.CloseAsync(token);

        await Should.NotThrowAsync(() => _host.StopAsync(token));
        await closing;
    }

    private DeadLetterQueueListener theListener()
    {
        return _host.Services.GetServices<IHostedService>().OfType<DeadLetterQueueListener>().Single();
    }

    private async Task waitForTheListenerToConsumeAsync()
    {
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();

        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var queue = await channel.QueueDeclarePassiveAsync(DeadLetterQueueName);
            if (queue.ConsumerCount > 0) return;

            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException("The dead letter queue listener never started consuming " + DeadLetterQueueName);
    }
}

/// <summary>
/// GH-5314, deterministically. The race in the issue is a channel that still reports open, then turns out to be
/// closed when its close is transmitted. That window is too narrow to hit reliably against a live broker.
/// </summary>
public class Bug_5314_dead_letter_listener_stop_with_closed_channel_and_connection
{
    [Fact]
    public async Task a_channel_and_connection_that_closed_underneath_the_stop_do_not_throw()
    {
        var goodbye = new ShutdownEventArgs(ShutdownInitiator.Application, 200, "Goodbye");

        var channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        channel.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AlreadyClosedException(goodbye));

        var connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new AlreadyClosedException(goodbye));

        var listener = new DeadLetterQueueListener(new RabbitMqTransport(), Substitute.For<IWolverineRuntime>(),
            new DeadLetterQueueRecoverySettings(), NullLogger<DeadLetterQueueListener>.Instance);

        setField(listener, "_channel", channel);
        setField(listener, "_connection", connection);

        var token = TestContext.Current.CancellationToken;
        await Should.NotThrowAsync(() => Task.WhenAll(listener.StopAsync(token), listener.StopAsync(token)));

        // Each was closed exactly once, by whichever stop claimed it, and still disposed
        await channel.Received(1).CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        await connection.Received(1).CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
        channel.Received(1).Dispose();
        connection.Received(1).Dispose();
    }

    private static void setField(DeadLetterQueueListener listener, string name, object value)
    {
        typeof(DeadLetterQueueListener).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(listener, value);
    }
}
