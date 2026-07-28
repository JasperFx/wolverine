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
/// Regression coverage for the "NullReferenceException in RabbitMqListener.CompleteAsync" report.
///
/// Deploy churn tears channels down constantly. An envelope that was already processed would try to
/// ACK through the listener's channel, but every rebuild path nulls that channel
/// (RabbitMqChannelAgent.teardownChannel), so `Channel!.BasicAckAsync(...)` dereferenced null. The
/// RetryBlock then retried the ACK — pointlessly, because delivery tags are scoped to the channel
/// they arrived on and can never be settled on any later channel. With no ACK, the broker redelivered,
/// which showed up as a steady "Duplicate incoming envelope detected" stream from the durable inbox.
///
/// The fix binds each delivery to the channel it actually arrived on (RabbitMqEnvelope.DeliveredOn)
/// and no-ops the settle when that channel is gone or has been replaced.
///
/// NOTE the second test below guards something subtler than the NRE: a bare null check would have
/// been WORSE than the crash. Delivery tags restart at 1 on each new channel, and CompleteAsync acks
/// with multiple: true, so replaying a stale tag against a rebuilt channel would ack every lower tag
/// on it — including messages still in the handler pipeline. That is silent message loss.
/// </summary>
public class Bug_3687_settling_a_delivery_from_a_dead_channel : IAsyncLifetime
{
    private readonly string _queueName = $"bug3687-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug3687";
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<Bug3687Message>().ToRabbitQueue(_queueName);
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

    [Fact]
    public async Task completing_a_delivery_after_the_channel_is_torn_down_does_not_throw()
    {
        var listener = findListener();
        var deliveringChannel = listener.Channel!;
        deliveringChannel.IsOpen.ShouldBeTrue();

        // A delivery that arrived on the channel the listener is holding right now.
        var envelope = new RabbitMqEnvelope(listener, 4200UL, deliveringChannel);
        listener.CanSettle(envelope).ShouldBeTrue("A live delivery on the current channel must be settleable");

        // Tear the listener down the way a pod shutdown does. teardownChannel() nulls Channel, which
        // is the exact state that produced the reported NullReferenceException.
        await listener.DisposeAsync();
        listener.Channel.ShouldBeNull();

        // Pre-fix this threw NullReferenceException out of `Channel!.BasicAckAsync(...)`, and the
        // RetryBlock then re-ran it three times before discarding.
        await Should.NotThrowAsync(() => listener.CompleteAsync(envelope));

        listener.CanSettle(envelope).ShouldBeFalse("A delivery cannot be settled once the channel is gone");
    }

    [Fact]
    public async Task a_stale_delivery_tag_is_never_settled_against_a_replaced_channel()
    {
        // Prove the pipe works before we break the channel.
        await _host.MessageBus().PublishAsync(new Bug3687Message("before-break"));
        (await waitForAsync(() => Bug3687MessageHandler.Contains("before-break"), 30.Seconds()))
            .ShouldBeTrue("Expected the message to be handled before the channel is broken");

        var listener = findListener();
        var originalChannel = listener.Channel!;

        // A delivery that arrived on the channel we are about to kill.
        var staleEnvelope = new RabbitMqEnvelope(listener, 4200UL, originalChannel);
        listener.CanSettle(staleEnvelope).ShouldBeTrue();

        // Force a broker-initiated channel shutdown without dropping the connection: a passive declare
        // against a queue that does not exist returns 404 NOT_FOUND and takes the channel down.
        try
        {
            await originalChannel.QueueDeclarePassiveAsync($"missing-{Guid.NewGuid():N}");
        }
        catch
        {
            // Expected — the passive declare fails and takes the channel with it.
        }

        var rebuilt = await waitForAsync(
            () => listener.Channel is { IsOpen: true } && !ReferenceEquals(listener.Channel, originalChannel),
            30.Seconds());
        rebuilt.ShouldBeTrue("The listener should have rebuilt onto a fresh channel");

        // The heart of it. The listener now holds a healthy, OPEN channel — so a null check would
        // happily pass here and ack tag 4200 against a channel whose tags restart at 1, sweeping up
        // every lower tag with multiple: true. The channel-identity check is what prevents that.
        listener.Channel!.IsOpen.ShouldBeTrue();
        listener.CanSettle(staleEnvelope)
            .ShouldBeFalse("A tag from the dead channel must not be settled against the replacement");

        await Should.NotThrowAsync(() => listener.CompleteAsync(staleEnvelope));

        // A delivery on the CURRENT channel is still settleable — the guard is not just latching off.
        var freshEnvelope = new RabbitMqEnvelope(listener, 1UL, listener.Channel!);
        listener.CanSettle(freshEnvelope).ShouldBeTrue();

        // And the listener is still genuinely working end to end.
        await _host.MessageBus().PublishAsync(new Bug3687Message("after-break"));
        (await waitForAsync(() => Bug3687MessageHandler.Contains("after-break"), 30.Seconds()))
            .ShouldBeTrue("Expected the listener to keep consuming after the channel rebuild");
    }
}

public record Bug3687Message(string Value);

public static class Bug3687MessageHandler
{
    private static readonly List<string> Received = new();

    public static bool Contains(string value)
    {
        lock (Received)
        {
            return Received.Contains(value);
        }
    }

    public static void Handle(Bug3687Message message)
    {
        lock (Received)
        {
            Received.Add(message.Value);
        }
    }
}
