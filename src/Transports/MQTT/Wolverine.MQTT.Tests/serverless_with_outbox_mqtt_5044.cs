using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.MQTT.Tests;

/// <summary>
///     PR #5044: how the MQTT sender protocol reports broker acceptance to the
///     ServerlessWithOutbox inline send and the durability pass, untenanted and broker-per-tenant
/// </summary>
[Collection("acceptance")]
public class serverless_with_outbox_mqtt_5044 : IAsyncLifetime
{
    private const string Schema = "serverless5044_mqtt";
    private readonly string _topic = $"serverless5044/mqtt/{Guid.NewGuid():N}";
    private int _portA;
    private int _portB;
    private LocalMqttBroker _brokerA = null!;
    private LocalMqttBroker _brokerB = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _portA = PortFinder.GetAvailablePort();
        _portB = PortFinder.GetAvailablePort();
        _brokerA = new LocalMqttBroker(_portA) { Logger = NullLogger.Instance };
        _brokerB = new LocalMqttBroker(_portB) { Logger = NullLogger.Instance };
        await _brokerA.StartAsync();
        await _brokerB.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _brokerA.DisposeAsync();
        await _brokerB.DisposeAsync();
    }

    private async Task<IHost> buildHostAsync(bool tenanted)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Serverless5044MqttHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                var mqtt = opts.UseMqttWithLocalBroker(_portA);
                if (tenanted)
                {
                    mqtt.TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
                        .AddTenant("tenantB", b => b.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));
                }

                opts.PublishMessage<Serverless5044MqttMessage>().ToMqttTopic(_topic).UseDurableOutbox();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await host.GetRuntime().Storage.Admin.ClearAllAsync();
        return host;
    }

    private Uri TopicUri => new($"mqtt://topic/{_topic}");

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select id, owner_id from {Schema}.wolverine_outgoing_envelopes";
        var rows = new Dictionary<Guid, int>();
        await using var reader = await cmd.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token)) rows[reader.GetGuid(0)] = reader.GetInt32(1);
        return rows;
    }

    private async Task makeOutboxStaleAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"update {Schema}.wolverine_outgoing_envelopes set \"timestamp\" = now() - interval '2 hours'";
        await cmd.ExecuteNonQueryAsync(Token);
    }

    private static async Task<Envelope> storeOutboxRowAsync(IHost host, Uri destination, int number,
        string? topicName = null)
    {
        var runtime = host.GetRuntime();
        var envelope = new Envelope(new Serverless5044MqttMessage(number))
        {
            Destination = destination,
            ContentType = "application/json",
            MessageType = typeof(Serverless5044MqttMessage).ToMessageTypeName(),
            TopicName = topicName
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, 0);
        return envelope;
    }

    private static List<Serverless5044MqttCountingProtocol> wrapAll(IHost host, Uri uri)
    {
        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(uri);
        var sender = (ISender)agent.GetType().GetProperty("Sender")!.GetValue(agent)!;

        var batched = new List<BatchedSender>();
        if (sender is TenantedSender tenanted)
        {
            batched.Add(tenanted.DefaultSender.ShouldBeOfType<BatchedSender>());
            batched.AddRange(tenanted.TenantSenders().Select(x => x.Value.ShouldBeOfType<BatchedSender>()));
        }
        else
        {
            batched.Add(sender.ShouldBeOfType<BatchedSender>());
        }

        return batched.Select(Serverless5044MqttCountingProtocol.Wrap).ToList();
    }

    [Fact]
    public async Task happy_path_sends_one_batch_and_leaves_no_outbox_rows()
    {
        await using var subscriber = await Serverless5044MqttSubscriber.StartAsync(_portA, _topic);
        using var host = await buildHostAsync(false);
        var counting = wrapAll(host, TopicUri).Single();

        await host.MessageBus().InvokeAsync(new Serverless5044MqttTrigger(5, 0), Token);

        counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });
        (await subscriber.WaitForCountAsync(5, 10.Seconds())).ShouldBe(5);
        (await outboxRowsAsync()).ShouldBeEmpty();

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task broker_down_leaves_every_row_and_a_pass_delivers_them_once_it_is_back()
    {
        using var host = await buildHostAsync(false);
        var counting = wrapAll(host, TopicUri).Single();

        await _brokerA.StopAsync();

        await host.MessageBus().InvokeAsync(new Serverless5044MqttTrigger(3, 0), Token);

        var rows = await outboxRowsAsync();
        rows.Count.ShouldBe(3);
        rows.Values.ShouldAllBe(x => x == 0);

        await _brokerA.StartAsync();
        await using var subscriber = await Serverless5044MqttSubscriber.StartAsync(_portA, _topic);

        // Give the managed client time to reconnect
        var mqtt = host.GetRuntime().Options.Transports.GetOrCreate<Internals.MqttTransport>();
        for (var i = 0; i < 100 && !mqtt.Client.IsConnected; i++) await Task.Delay(200, Token);
        mqtt.Client.IsConnected.ShouldBeTrue();

        await makeOutboxStaleAsync();
        await host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        (await subscriber.WaitForCountAsync(3, 10.Seconds())).ShouldBe(3);
        (await outboxRowsAsync()).ShouldBeEmpty();
        counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 3, 3 });

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task a_publish_that_fails_part_way_through_a_batch_fails_the_ones_already_published()
    {
        await using var subscriber = await Serverless5044MqttSubscriber.StartAsync(_portA, $"{_topic}/#");
        using var host = await buildHostAsync(false);
        var counting = wrapAll(host, TopicUri).Single();

        // 4 rows for one sender. The third carries a topic name MQTT refuses ('#' only as the last level),
        // so the client throws on it after the first two were published and acknowledged
        await storeOutboxRowAsync(host, TopicUri, 1, $"{_topic}/ok");
        await storeOutboxRowAsync(host, TopicUri, 2, $"{_topic}/ok");
        await storeOutboxRowAsync(host, TopicUri, 3, $"{_topic}/#/bad");
        await storeOutboxRowAsync(host, TopicUri, 4, $"{_topic}/ok");

        await makeOutboxStaleAsync();
        await host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        var afterFirst = await subscriber.WaitForCountAsync(2, 5.Seconds());
        var rowsAfterFirst = await outboxRowsAsync();
        afterFirst.ShouldBe(2);

        await makeOutboxStaleAsync();
        await host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        var afterSecond = await subscriber.WaitForCountAsync(4, 5.Seconds());

        // EXPECTED: the two the broker acknowledged are not left to be sent again
        rowsAfterFirst.Count.ShouldBeLessThanOrEqualTo(2,
            $"after the first pass the subscriber had {afterFirst} messages but {rowsAfterFirst.Count} outbox rows were left; after a second pass it has {afterSecond}. Batches: {string.Join(",", counting.Batches.Select(x => x.Length))}");

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task tenanted_inline_send_goes_to_each_tenants_broker_in_one_batch_each()
    {
        await using var onA = await Serverless5044MqttSubscriber.StartAsync(_portA, _topic);
        await using var onB = await Serverless5044MqttSubscriber.StartAsync(_portB, _topic);
        using var host = await buildHostAsync(true);
        var counting = wrapAll(host, TopicUri);

        await host.MessageBus().InvokeAsync(new Serverless5044MqttTrigger(2, 3), Token);

        (await onA.WaitForCountAsync(2, 10.Seconds())).ShouldBe(2);
        (await onB.WaitForCountAsync(3, 10.Seconds())).ShouldBe(3);
        (await outboxRowsAsync()).ShouldBeEmpty();

        // One round trip per tenant connection
        counting.Select(x => x.Batches.Sum(b => b.Length)).OrderBy(x => x).ToArray().ShouldBe(new[] { 2, 3 });
        counting.Sum(x => x.Batches.Count).ShouldBe(2);

        await host.StopAsync(Token);
    }

    [Fact]
    public async Task tenanted_with_the_tenant_broker_down_only_its_rows_are_left_and_a_pass_delivers_them()
    {
        await using var onA = await Serverless5044MqttSubscriber.StartAsync(_portA, _topic);
        using var host = await buildHostAsync(true);

        await _brokerB.StopAsync();

        await host.MessageBus().InvokeAsync(new Serverless5044MqttTrigger(2, 3), Token);

        (await onA.WaitForCountAsync(2, 10.Seconds())).ShouldBe(2);
        var rows = await outboxRowsAsync();
        rows.Count.ShouldBe(3);
        rows.Values.ShouldAllBe(x => x == 0);

        await _brokerB.StartAsync();
        await using var onB = await Serverless5044MqttSubscriber.StartAsync(_portB, _topic);

        var mqtt = host.GetRuntime().Options.Transports.GetOrCreate<Internals.MqttTransport>();
        var tenantClient = mqtt.Tenants["tenantB"].Client!;
        for (var i = 0; i < 100 && !tenantClient.IsConnected; i++) await Task.Delay(200, Token);
        tenantClient.IsConnected.ShouldBeTrue();

        await makeOutboxStaleAsync();
        await host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        (await onB.WaitForCountAsync(3, 10.Seconds())).ShouldBe(3);
        (await outboxRowsAsync()).ShouldBeEmpty();

        // and nothing more went to the default broker
        (await onA.WaitForCountAsync(3, 2.Seconds())).ShouldBe(2);

        await host.StopAsync(Token);
    }
}

public record Serverless5044MqttTrigger(int DefaultCount, int TenantBCount);

public record Serverless5044MqttMessage(int Number);

public static class Serverless5044MqttHandler
{
    public static async Task Handle(Serverless5044MqttTrigger trigger, IMessageContext context)
    {
        for (var i = 0; i < trigger.DefaultCount; i++)
        {
            await context.SendAsync(new Serverless5044MqttMessage(i));
        }

        for (var i = 0; i < trigger.TenantBCount; i++)
        {
            await context.SendAsync(new Serverless5044MqttMessage(100 + i), new DeliveryOptions { TenantId = "tenantB" });
        }
    }
}

internal class Serverless5044MqttCountingProtocol : ISenderProtocol
{
    private readonly ISenderProtocol _inner;

    public Serverless5044MqttCountingProtocol(ISenderProtocol inner) => _inner = inner;

    public ConcurrentQueue<Envelope[]> Calls { get; } = new();
    public List<Envelope[]> Batches => Calls.ToList();

    public Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        Calls.Enqueue(batch.Messages.ToArray());
        return _inner.SendBatchAsync(callback, batch);
    }

    public static Serverless5044MqttCountingProtocol Wrap(BatchedSender batched)
    {
        var field = typeof(BatchedSender).GetField("_protocol", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counting = new Serverless5044MqttCountingProtocol((ISenderProtocol)field.GetValue(batched)!);
        field.SetValue(batched, counting);
        return counting;
    }
}

internal class Serverless5044MqttSubscriber : IAsyncDisposable
{
    private readonly IManagedMqttClient _client;
    private readonly ConcurrentQueue<string> _messages = new();

    private Serverless5044MqttSubscriber(IManagedMqttClient client) => _client = client;

    public static async Task<Serverless5044MqttSubscriber> StartAsync(int port, string topic)
    {
        var client = new MqttFactory().CreateManagedMqttClient();
        var subscriber = new Serverless5044MqttSubscriber(client);

        client.ApplicationMessageReceivedAsync += e =>
        {
            subscriber._messages.Enqueue(Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
            return Task.CompletedTask;
        };

        await client.StartAsync(new ManagedMqttClientOptionsBuilder()
            .WithClientOptions(o => o.WithTcpServer("127.0.0.1", port)).Build());
        await client.SubscribeAsync(topic);

        // Wait for the subscription to be in place
        for (var i = 0; i < 50 && (!client.IsConnected || client.PendingApplicationMessagesCount > 0); i++)
        {
            await Task.Delay(100);
        }

        await Task.Delay(500);

        return subscriber;
    }

    // Waits until at least `expected` have arrived or the timeout passes, then a little longer for extras
    public async Task<int> WaitForCountAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && _messages.Count < expected) await Task.Delay(50);

        await Task.Delay(1.Seconds());
        return _messages.Count;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.StopAsync();
        _client.Dispose();
    }
}
