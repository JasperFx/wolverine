using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// Regression coverage for #3171: a RabbitMQ channel can shut down while the
/// underlying connection stays alive (a channel-only shutdown). Before the fix the
/// agent latched into AgentState.Disconnected forever because EnsureInitiated()
/// short-circuited on the stale, non-null channel and HandleChannelShutdownAsync
/// never rebuilt. Senders threw "... is disconnected" on every send and listeners
/// silently stopped consuming until a process restart.
/// </summary>
public class Bug_3171_channel_only_shutdown_recovery : IAsyncLifetime
{
    private readonly string _queueName = $"bug3171-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug3171";
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug3171Message>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName);

                opts.LocalRoutingConventionDisabled = true;
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private RabbitMqSender FindSender()
    {
        var runtime = _host.GetRuntime();
        var uri = new Uri($"rabbitmq://queue/{_queueName}");
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(uri);
        var inner = agent switch
        {
            InlineSendingAgent i => i.Sender,
            SendingAgent s => s.Sender,
            _ => throw new InvalidOperationException($"Unexpected sending agent type {agent.GetType()}")
        };

        return inner.ShouldBeOfType<RabbitMqSender>();
    }

    private RabbitMqListener FindListener()
    {
        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri($"rabbitmq://queue/{_queueName}"));
        return ((ListeningAgent)agent).Listener.ShouldBeOfType<RabbitMqListener>();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }

        return condition();
    }

    // The single host both sends and receives over RabbitMQ; rather than rely on
    // cross-transport tracking correlation, publish and poll the handler's record.
    private async Task PublishAndExpectHandledAsync(string value)
    {
        await _host.MessageBus().PublishAsync(new Bug3171Message(value));

        var handled = await WaitForAsync(() =>
        {
            lock (Bug3171MessageHandler.Received)
            {
                return Bug3171MessageHandler.Received.Contains(value);
            }
        }, 30.Seconds());

        handled.ShouldBeTrue($"Expected the message '{value}' to be received and handled");
    }

    [Fact]
    public async Task sender_recovers_after_a_channel_only_shutdown()
    {
        var sender = FindSender();
        await sender.EnsureInitiated();
        sender.State.ShouldBe(AgentState.Connected);
        sender.Channel!.IsOpen.ShouldBeTrue();

        // Close ONLY the channel; the connection underneath stays alive. This is the
        // exact #3171 trigger: the channel goes away without a callback exception.
        await sender.Channel!.CloseAsync(200, "channel-only shutdown", false, CancellationToken.None);

        (await WaitForAsync(() => sender.State == AgentState.Disconnected, 5.Seconds())).ShouldBeTrue();

        // The stale channel is retained (non-null but closed) — pre-fix this is what
        // made EnsureInitiated() a permanent no-op.
        sender.Channel.ShouldNotBeNull();
        sender.Channel!.IsOpen.ShouldBeFalse();

        // The fix: EnsureInitiated() treats a closed channel as unhealthy and rebuilds.
        await sender.EnsureInitiated();

        sender.State.ShouldBe(AgentState.Connected);
        sender.Channel.ShouldNotBeNull();
        sender.Channel!.IsOpen.ShouldBeTrue();

        // And a real send now succeeds instead of throwing "... is disconnected".
        await PublishAndExpectHandledAsync("after-sender-recovery");
    }

    [Fact]
    public async Task listener_resumes_consuming_after_an_unexpected_channel_shutdown()
    {
        // Prove the pipe works before we break the channel.
        await PublishAndExpectHandledAsync("before-break");

        var listener = FindListener();
        var originalChannel = listener.Channel!;
        originalChannel.IsOpen.ShouldBeTrue();

        // Force a broker-initiated (non-Application) channel shutdown without dropping the
        // connection: a passive declare against a queue that does not exist returns 404
        // NOT_FOUND, which makes the broker close the channel. This is the listener flavor
        // of #3171 — the listener sits blocked and will not self-heal on its own.
        try
        {
            await originalChannel.QueueDeclarePassiveAsync($"missing-{Guid.NewGuid():N}");
        }
        catch
        {
            // Expected — the passive declare fails and takes the channel down with it.
        }

        // The push-heal hook should rebuild the listener on a fresh, open channel.
        var rebuilt = await WaitForAsync(
            () => listener.Channel is { IsOpen: true } && !ReferenceEquals(listener.Channel, originalChannel),
            30.Seconds());
        rebuilt.ShouldBeTrue("The listener should have rebuilt its channel after an unexpected shutdown");
        listener.State.ShouldBe(AgentState.Connected);

        // The real proof: messages flow again without any process restart.
        await PublishAndExpectHandledAsync("after-break");
    }
}

public record Bug3171Message(string Value);

public static class Bug3171MessageHandler
{
    public static readonly List<string> Received = new();

    public static void Handle(Bug3171Message message)
    {
        lock (Received)
        {
            Received.Add(message.Value);
        }
    }
}
