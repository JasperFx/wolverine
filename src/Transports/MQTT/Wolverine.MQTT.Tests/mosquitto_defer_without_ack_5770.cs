using System.Collections.Concurrent;
using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using Shouldly;
using Wolverine.ErrorHandling;

namespace Wolverine.MQTT.Tests;

/// <summary>
///     GH-5770, against a real Mosquitto broker. The first test pins down the MQTT semantics the fix relies on; the
///     second runs a requeue end to end.
/// </summary>
[Collection("mosquitto")]
public class mosquitto_defer_without_ack_5770
{
    private static MqttClientOptions persistentSession(string clientId) => new MqttClientOptionsBuilder()
        .WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port)
        .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
        .WithClientId(clientId)
        .WithCleanStart(false)
        .WithSessionExpiryInterval(300)
        .Build();

    /// <summary>
    ///     With <c>AutoAcknowledge = false</c> MQTTnet sends no PUBACK until <c>AcknowledgeAsync</c> is called. The
    ///     broker does NOT redeliver an unacknowledged QoS 1 message on the same connection; it does when a
    ///     persistent session resumes. That is why a stopped listener's handed-back messages are taken over by its
    ///     replacement on the same connection, and left to the broker otherwise.
    /// </summary>
    [Fact]
    public async Task an_unacknowledged_message_comes_back_only_when_the_session_resumes()
    {
        var topic = $"defer5770/raw/{Guid.NewGuid():N}";
        var clientId = $"defer5770-{Guid.NewGuid():N}";
        var factory = new MqttClientFactory();
        var received = new ConcurrentQueue<bool>();

        using var subscriber = factory.CreateMqttClient();
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            args.AutoAcknowledge = false;
            received.Enqueue(args.ApplicationMessage.Dup);
            return Task.CompletedTask;
        };

        await subscriber.ConnectAsync(persistentSession(clientId), TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce,
            TestContext.Current.CancellationToken);

        using var publisher = factory.CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port).Build(),
            TestContext.Current.CancellationToken);
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes("unacked"))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(),
            TestContext.Current.CancellationToken);

        await waitForAsync(() => received.Count == 1);

        // Never acknowledged, and the connection stays up: nothing comes back
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
        received.Count.ShouldBe(1);

        // The session resumes on a new connection: the broker redelivers it
        await subscriber.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ConnectAsync(persistentSession(clientId), TestContext.Current.CancellationToken);

        await waitForAsync(() => received.Count == 2);
        received.Last().ShouldBeTrue("the redelivery is flagged DUP");

        await subscriber.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(),
            TestContext.Current.CancellationToken);
        await publisher.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A requeue on an Inline listener used to PUBACK first and re-feed the message inline. Now the message is
    ///     offered again unacknowledged and acknowledged when the retry succeeds, so a host that resumes the same
    ///     session afterwards is not handed it again.
    /// </summary>
    [Fact]
    public async Task a_requeued_message_is_handled_and_then_acknowledged()
    {
        var topic = $"defer5770/requeue/{Guid.NewGuid():N}";
        var clientId = $"defer5770-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();

        IHost buildHost() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt => mqtt.WithClientOptions(client => client
                    .WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port)
                    .WithClientId(clientId)
                    .WithCleanStart(false)
                    .WithSessionExpiryInterval(300)));

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MqttRequeue5770Handler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.OnException<MqttRequeue5770Exception>().Requeue();

                opts.PublishMessage<MqttRequeue5770Message>().ToMqttTopic(topic);
                opts.ListenToMqttTopic(topic).ProcessInline();
            }).Build();

        using (var host = buildHost())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);

            await host.MessageBus().PublishAsync(new MqttRequeue5770Message(id));

            await waitForAsync(() => MqttRequeue5770Handler.Successes(id) == 1);
            MqttRequeue5770Handler.Attempts(id).ShouldBe(2);

            // Give the PUBACK time to reach the broker before the session is suspended
            await Task.Delay(500, TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var host = buildHost())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);

            await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
            MqttRequeue5770Handler.Attempts(id).ShouldBe(2, "an acknowledged message must not come back");

            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     The loss itself. A message whose requeues are still failing when the host stops used to be gone: every
    ///     requeue had already acknowledged it. Now it is still unacknowledged when the connection closes, so the
    ///     broker hands it to whoever resumes the session.
    /// </summary>
    [Fact]
    public async Task a_message_still_being_requeued_at_shutdown_comes_back_when_the_session_resumes()
    {
        var topic = $"defer5770/shutdown/{Guid.NewGuid():N}";
        var clientId = $"defer5770-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();

        IHost buildHost() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt => mqtt.WithClientOptions(client => client
                    .WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port)
                    .WithClientId(clientId)
                    .WithCleanStart(false)
                    .WithSessionExpiryInterval(300)));

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MqttUnavailable5770Handler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.OnException<MqttRequeue5770Exception>().RequeueIndefinitely();

                opts.PublishMessage<MqttUnavailable5770Message>().ToMqttTopic(topic);
                opts.ListenToMqttTopic(topic).ProcessInline();
            }).Build();

        using (var host = buildHost())
        {
            await host.StartAsync(TestContext.Current.CancellationToken);

            await host.MessageBus().PublishAsync(new MqttUnavailable5770Message(id));

            // Failing and being requeued
            await waitForAsync(() => MqttUnavailable5770Handler.Attempts(id) >= 2);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Resume the session with a raw client, which sees the broker's DUP flag. A second Wolverine host
        // resuming the session is covered by mosquitto_backlog_before_listener_5790
        var redelivered = new ConcurrentQueue<bool>();
        using var resumed = new MqttClientFactory().CreateMqttClient();
        resumed.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic == topic) redelivered.Enqueue(args.ApplicationMessage.Dup);
            return Task.CompletedTask;
        };

        await resumed.ConnectAsync(persistentSession(clientId), TestContext.Current.CancellationToken);

        await waitForAsync(() => !redelivered.IsEmpty);
        redelivered.First().ShouldBeTrue("the broker is redelivering the message it never had a PUBACK for");

        await resumed.DisconnectAsync(new MqttClientDisconnectOptionsBuilder()
            .WithSessionExpiryInterval(0).Build(), TestContext.Current.CancellationToken);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "timed out");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

public record MqttRequeue5770Message(Guid Id);

public class MqttRequeue5770Exception : Exception;

public static class MqttRequeue5770Handler
{
    private static readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, int> _successes = new();

    public static int Attempts(Guid id) => _attempts.GetValueOrDefault(id);
    public static int Successes(Guid id) => _successes.GetValueOrDefault(id);

    public static void Handle(MqttRequeue5770Message message)
    {
        // The first attempt fails and is requeued
        if (_attempts.AddOrUpdate(message.Id, 1, (_, x) => x + 1) == 1)
        {
            throw new MqttRequeue5770Exception();
        }

        _successes.AddOrUpdate(message.Id, 1, (_, x) => x + 1);
    }
}

public record MqttUnavailable5770Message(Guid Id);

public static class MqttUnavailable5770Handler
{
    private static readonly ConcurrentDictionary<Guid, int> _attempts = new();

    public static int Attempts(Guid id) => _attempts.GetValueOrDefault(id);

    public static void Handle(MqttUnavailable5770Message message)
    {
        _attempts.AddOrUpdate(message.Id, 1, (_, x) => x + 1);

        // Never succeeds: the message is still being requeued when the host stops
        throw new MqttRequeue5770Exception();
    }
}
