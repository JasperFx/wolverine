using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// Regression coverage for #3391, the two follow-ups from the #3370 review of the
/// callback-exception eager restart in RabbitMqChannelAgent:
///
/// 1. The ConnectionMonitor tracking invariant. An agent MUST stay tracked across a
///    callback-exception restart, success or failure — only tracked agents are rebuilt by
///    connection recovery, so an untracked agent is one connection drop away from ghosting.
///    Before #3370 even a SUCCESSFUL restart left the agent untracked.
///
/// 2. The eager restart of a *listener* used to only swap in a fresh channel — no re-declare,
///    no BasicConsume. That left an open channel with ZERO consumers while State == Connected:
///    a silently dead listener.
///
/// Both are driven by a genuine channel callback exception: RabbitMQ.Client surfaces an
/// exception thrown from one of its own event handlers as Channel.CallbackExceptionAsync, and
/// an unroutable mandatory publish is a deterministic way to get one of those handlers invoked.
/// Note that the channel itself stays open, so the #3171/#3187 shutdown push-heal does NOT fire
/// here — the eager restart path is on its own, which is exactly the point.
/// </summary>
public class Bug_3391_callback_exception_restart : IAsyncLifetime
{
    private readonly string _queueName = $"bug3391-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug3391";
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug3391Message>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName);

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private RabbitMqListener findListener()
    {
        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{_queueName}"));
        return ((ListeningAgent)agent).Listener.ShouldBeOfType<RabbitMqListener>();
    }

    private ConnectionMonitor listeningConnection()
    {
        var transport = _host.GetRuntime().Options.RabbitMqTransport();
        return transport.UseSenderConnectionOnly ? transport.SendingConnection : transport.ListeningConnection;
    }

    private static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    private async Task publishAndExpectHandledAsync(string value)
    {
        await _host.MessageBus().PublishAsync(new Bug3391Message(value));

        var handled = await waitForAsync(() =>
        {
            lock (Bug3391MessageHandler.Received)
            {
                return Bug3391MessageHandler.Received.Contains(value);
            }
        }, 30.Seconds());

        handled.ShouldBeTrue($"Expected the message '{value}' to be received and handled");
    }

    /// <summary>
    /// Provoke a real callback exception on the agent's channel without killing the channel:
    /// subscribe a handler to BasicReturnAsync that throws, then publish a mandatory message to
    /// a routing key that resolves to nothing. The broker returns it, our handler throws, and
    /// RabbitMQ.Client raises CallbackExceptionAsync on the channel.
    /// </summary>
    private static async Task provokeCallbackExceptionAsync(IChannel channel)
    {
        channel.BasicReturnAsync += (_, _) => throw new DivideByZeroException("Boom, from a Rabbit MQ callback");

        var properties = new BasicProperties();
        await channel.BasicPublishAsync("", $"nowhere-{Guid.NewGuid():N}", true, properties,
            ReadOnlyMemory<byte>.Empty, CancellationToken.None);
    }

    /// <summary>
    /// The number of consumers the broker sees on the queue, read over a throwaway channel.
    /// </summary>
    private async Task<uint> consumerCountAsync()
    {
        await using var channel = await listeningConnection().CreateChannelAsync();
        var result = await channel.QueueDeclarePassiveAsync(_queueName);
        return result.ConsumerCount;
    }

    [Fact]
    public async Task agent_stays_tracked_by_the_connection_monitor_across_a_callback_exception_restart()
    {
        var listener = findListener();
        var monitor = listeningConnection();

        monitor.TrackedAgents.ShouldContain(listener);

        var originalChannel = listener.Channel!;
        originalChannel.IsOpen.ShouldBeTrue();

        await provokeCallbackExceptionAsync(originalChannel);

        var restarted = await waitForAsync(
            () => listener.Channel is { IsOpen: true } && !ReferenceEquals(listener.Channel, originalChannel),
            30.Seconds());
        restarted.ShouldBeTrue("The agent should have eagerly restarted its channel after a callback exception");
        listener.State.ShouldBe(AgentState.Connected);

        // The invariant. Only agents tracked by the ConnectionMonitor are rebuilt by
        // connectionOnRecoverySucceededAsync, so dropping one here — even on a restart that
        // SUCCEEDED — leaves it one connection drop away from being ghosted forever (#3370).
        monitor.TrackedAgents.ShouldContain(listener);
    }

    [Fact]
    public async Task listener_still_consumes_after_a_successful_eager_restart()
    {
        // Prove the pipe works before we break anything.
        await publishAndExpectHandledAsync("before-callback-exception");

        var listener = findListener();
        var originalChannel = listener.Channel!;

        (await consumerCountAsync()).ShouldBe(1u);

        await provokeCallbackExceptionAsync(originalChannel);

        var restarted = await waitForAsync(
            () => listener.Channel is { IsOpen: true } && !ReferenceEquals(listener.Channel, originalChannel),
            30.Seconds());
        restarted.ShouldBeTrue("The listener should have eagerly restarted its channel after a callback exception");
        listener.State.ShouldBe(AgentState.Connected);

        // The bug: the eager restart only opened a fresh channel. No re-declare, no BasicConsume.
        // The queue was left with ZERO consumers while the agent happily reported Connected.
        var deadline = DateTimeOffset.UtcNow + 30.Seconds();
        uint consumers;
        while ((consumers = await consumerCountAsync()) != 1u && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        consumers.ShouldBe(1u, "The restarted listener should be consuming from the queue again");

        // ...and the real proof: messages still flow, with no process restart.
        await publishAndExpectHandledAsync("after-callback-exception");
    }
}

public record Bug3391Message(string Value);

public static class Bug3391MessageHandler
{
    public static readonly List<string> Received = new();

    public static void Handle(Bug3391Message message)
    {
        lock (Received)
        {
            Received.Add(message.Value);
        }
    }
}
