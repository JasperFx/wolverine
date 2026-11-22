using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;

namespace Wolverine.MQTT.Tests;

/// <summary>
///     GH-5790, against a real Mosquitto broker. When a persistent session resumes, the broker resends what was
///     in flight on it the moment the connection comes up, which is before Wolverine has registered its listeners. GH-4802 left those messages
///     unacknowledged "so the broker redelivers them", but a broker only resends an unacknowledged message when
///     the session resumes on a NEW connection, so they sat in flight until the next reconnect. Now they are held
///     and handed to the listener when it registers.
/// </summary>
[Collection("mosquitto")]
public class mosquitto_backlog_before_listener_5790
{
    /// <summary>
    ///     The first host is still requeueing the messages when it stops, so they are in flight on its session,
    ///     unacknowledged (GH-5770). The broker resends those the moment the second host resumes the session,
    ///     ahead of any listener registration.
    /// </summary>
    [Fact]
    public async Task a_resumed_sessions_backlog_is_handled_once_the_listener_registers()
    {
        var topic = $"backlog5790/{Guid.NewGuid():N}";
        var clientId = $"backlog5790-{Guid.NewGuid():N}";
        var session = Guid.NewGuid();
        const int backlog = 5;

        IHost receiver(bool publishes = false) => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(mqtt => mqtt.WithClientOptions(client => client
                    .WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port)
                    .WithClientId(clientId)
                    .WithCleanStart(false)
                    .WithSessionExpiryInterval(300)));

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MqttBacklog5790Handler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.OnException<MqttBacklog5790Exception>().RequeueIndefinitely();

                if (publishes) opts.PublishMessage<MqttBacklog5790Message>().ToMqttTopic(topic);
                opts.ListenToMqttTopic(topic).ProcessInline();
            }).Build();

        MqttBacklog5790Handler.Available = false;

        using (var first = receiver(publishes: true))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);

            for (var i = 0; i < backlog; i++)
            {
                await first.MessageBus().PublishAsync(new MqttBacklog5790Message(session, i));
            }

            await waitForAsync(() => MqttBacklog5790Handler.Attempts(session) >= backlog);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        MqttBacklog5790Handler.Available = true;

        // A second Wolverine host resumes the session. The broker resends the unacknowledged messages before
        // the listener registers, and they are handled on this same connection
        using (var second = receiver())
        {
            await second.StartAsync(TestContext.Current.CancellationToken);

            await waitForAsync(() => MqttBacklog5790Handler.Handled(session) == backlog);

            // Give the PUBACKs time to reach the broker before the session is suspended again
            await Task.Delay(500, TestContext.Current.CancellationToken);
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        // Acknowledged once handled: the next host to resume the session is not handed them again
        using (var third = receiver())
        {
            await third.StartAsync(TestContext.Current.CancellationToken);
            await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
            await third.StopAsync(TestContext.Current.CancellationToken);
        }

        MqttBacklog5790Handler.Handled(session).ShouldBe(backlog);
    }

    private static async Task waitForAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline,
                "timed out waiting for the backlog, which only a reconnect would otherwise bring back");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

public record MqttBacklog5790Message(Guid Session, int Number);

public class MqttBacklog5790Exception : Exception;

public static class MqttBacklog5790Handler
{
    private static readonly ConcurrentDictionary<Guid, int> _attempts = new();
    private static readonly ConcurrentDictionary<Guid, int> _handled = new();

    public static volatile bool Available;

    public static int Attempts(Guid session) => _attempts.GetValueOrDefault(session);
    public static int Handled(Guid session) => _handled.GetValueOrDefault(session);

    public static void Handle(MqttBacklog5790Message message)
    {
        _attempts.AddOrUpdate(message.Session, 1, (_, x) => x + 1);

        if (!Available) throw new MqttBacklog5790Exception();

        _handled.AddOrUpdate(message.Session, 1, (_, x) => x + 1);
    }
}
