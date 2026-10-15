using System.Collections.Concurrent;
using System.Reflection;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using MessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
///     PR #5044: how the SNS sender protocol reports broker acceptance to the
///     ServerlessWithOutbox inline send and the durability pass
/// </summary>
public class serverless_with_outbox_sns_5044 : IAsyncLifetime
{
    private const string Schema = "serverless5044_sns";
    private readonly string _name = $"serverless5044-sns-{Guid.NewGuid():N}";
    private IHost _host = null!;
    private AmazonSnsTransport _transport = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Plain => _name;
    private string Wide => _name + "-wide";
    private string Poison => _name + "-poison";

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Serverless5044SnsHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.UseAmazonSqsTransportLocally().AutoProvision();
                opts.UseAmazonSnsTransportLocally().AutoProvision();

                opts.PublishMessage<Serverless5044SnsMessage>().ToSnsTopic(Plain).SubscribeSqsQueue(Plain)
                    .UseDurableOutbox();

                // A user who raises MessageBatchSize past PublishBatch's 10 entry limit
                opts.PublishMessage<Serverless5044SnsWideMessage>().ToSnsTopic(Wide).SubscribeSqsQueue(Wide)
                    .MessageBatchSize(25).UseDurableOutbox();

                opts.PublishMessage<Serverless5044SnsPoisonMessage>().ToSnsTopic(Poison).SubscribeSqsQueue(Poison)
                    .InteropWith(new Serverless5044PoisonMapper()).UseDurableOutbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
        _transport = _host.GetRuntime().Options.AmazonSnsTransport();
    }

    public async ValueTask DisposeAsync()
    {
        var options = _host.GetRuntime().Options;
        foreach (var queue in options.AmazonSqsTransport().Queues.Where(x => x.QueueName.StartsWith("serverless5044")))
        {
            try { await queue.TeardownAsync(NullLogger.Instance); } catch { }
        }

        foreach (var topic in _transport.Topics.Where(x => x.TopicName.StartsWith("serverless5044")))
        {
            try { await topic.TeardownAsync(NullLogger.Instance); } catch { }
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    private Serverless5044SnsCountingProtocol wrap(string topicName)
    {
        return Serverless5044SnsCountingProtocol.Wrap(_host, _transport.Topics[topicName].Uri);
    }

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

    // What reached the SQS queue subscribed to the topic
    private async Task<int> drainAsync(string queueName, int expected, TimeSpan wait)
    {
        var sqs = _transport.SqsClient!;
        var url = (await sqs.GetQueueUrlAsync(queueName, Token)).QueueUrl;
        var count = 0;
        var deadline = DateTimeOffset.UtcNow.Add(wait);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest(url) { MaxNumberOfMessages = 10, WaitTimeSeconds = 1 }, Token);
            var messages = response.Messages ?? [];
            count += messages.Count;
            foreach (var message in messages) await sqs.DeleteMessageAsync(url, message.ReceiptHandle, Token);
            if (messages.Count == 0 && count >= expected) break;
        }

        return count;
    }

    [Fact]
    public async Task happy_path_sends_one_batch_and_leaves_no_outbox_rows()
    {
        var counting = wrap(Plain);

        await _host.MessageBus().InvokeAsync(new Serverless5044SnsTrigger(5, 0, 0), Token);

        counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 5 });
        (await drainAsync(Plain, 5, 15.Seconds())).ShouldBe(5);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task message_batch_size_over_the_publish_batch_limit()
    {
        var counting = wrap(Wide);

        await _host.MessageBus().InvokeAsync(new Serverless5044SnsTrigger(0, 15, 0), Token);

        var delivered = await drainAsync(Wide, 15, 10.Seconds());
        var rows = await outboxRowsAsync();

        // And a pass, which groups the same 15 rows into the same oversized PublishBatch
        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        var deliveredByPass = await drainAsync(Wide, 15, 5.Seconds());
        var rowsAfterPass = await outboxRowsAsync();

        // EXPECTED: all 15 delivered, the protocol splitting what it is handed into PublishBatch calls of 10
        delivered.ShouldBe(15,
            $"inline: {delivered} delivered, {rows.Count} outbox rows left; after a pass: {deliveredByPass} delivered, {rowsAfterPass.Count} rows left; protocol calls: {string.Join(",", counting.Batches.Select(x => x.Length))}");
    }

    [Fact]
    public async Task an_envelope_the_mapper_cannot_map_is_reported_successful()
    {
        var counting = wrap(Poison);

        // Outbox rows as a failed inline send leaves them: 2 good, 1 the mapper throws on
        var runtime = _host.GetRuntime();
        var uri = _transport.Topics[Poison].Uri;
        foreach (var number in new[] { 1, Serverless5044PoisonMapper.PoisonNumber, 2 })
        {
            var envelope = new Envelope(new Serverless5044SnsPoisonMessage(number))
            {
                Destination = uri, ContentType = "application/json",
                MessageType = typeof(Serverless5044SnsPoisonMessage).ToMessageTypeName()
            };
            envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
            await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, 0);
        }

        await makeOutboxStaleAsync();
        var report = await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        var delivered = await drainAsync(Poison, 2, 10.Seconds());

        // Observed facts, recorded: 2 delivered, and all 3 rows deleted -- the unmappable one is counted sent
        delivered.ShouldBe(2);
        (await outboxRowsAsync()).ShouldBeEmpty();
        report.OutgoingSent.ShouldBe(3);
        counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 3 });
    }

    [Fact]
    public async Task a_topic_that_no_longer_exists_leaves_every_row()
    {
        var topic = _transport.Topics[Plain];
        await _transport.SnsClient!.DeleteTopicAsync(topic.TopicArn, Token);

        await _host.MessageBus().InvokeAsync(new Serverless5044SnsTrigger(3, 0, 0), Token);

        var rows = await outboxRowsAsync();
        rows.Count.ShouldBe(3);
        rows.Values.ShouldAllBe(x => x == 0);
    }
}

public record Serverless5044SnsTrigger(int Plain, int Wide, int Unused);

public record Serverless5044SnsMessage(int Number);

public record Serverless5044SnsWideMessage(int Number);

public record Serverless5044SnsPoisonMessage(int Number);

public static class Serverless5044SnsHandler
{
    public static OutgoingMessages Handle(Serverless5044SnsTrigger trigger)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < trigger.Plain; i++) messages.Add(new Serverless5044SnsMessage(i));
        for (var i = 0; i < trigger.Wide; i++) messages.Add(new Serverless5044SnsWideMessage(i));
        return messages;
    }
}

internal class Serverless5044PoisonMapper : ISnsEnvelopeMapper
{
    public const int PoisonNumber = 99;
    private readonly DefaultSnsEnvelopeMapper _inner = new();

    public string BuildMessageBody(Envelope envelope)
    {
        var message = envelope.Message as Serverless5044SnsPoisonMessage
                      ?? System.Text.Json.JsonSerializer.Deserialize<Serverless5044SnsPoisonMessage>(envelope.Data!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (message!.Number == PoisonNumber) throw new InvalidOperationException("serverless5044 unmappable envelope");
        return _inner.BuildMessageBody(envelope);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
        => _inner.ToAttributes(envelope);

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
        => _inner.ReadEnvelopeData(envelope, messageBody, attributes);
}

internal class Serverless5044SnsCountingProtocol : ISenderProtocol
{
    private readonly ISenderProtocol _inner;

    public Serverless5044SnsCountingProtocol(ISenderProtocol inner) => _inner = inner;

    public ConcurrentQueue<Envelope[]> Calls { get; } = new();
    public List<Envelope[]> Batches => Calls.ToList();

    public Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        Calls.Enqueue(batch.Messages.ToArray());
        return _inner.SendBatchAsync(callback, batch);
    }

    public static Serverless5044SnsCountingProtocol Wrap(IHost host, Uri uri)
    {
        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(uri);
        var sender = (ISender)agent.GetType().GetProperty("Sender")!.GetValue(agent)!;
        var batched = sender.ShouldBeOfType<BatchedSender>();
        var field = typeof(BatchedSender).GetField("_protocol", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counting = new Serverless5044SnsCountingProtocol((ISenderProtocol)field.GetValue(batched)!);
        field.SetValue(batched, counting);
        return counting;
    }
}
