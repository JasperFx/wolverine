using System.Collections.Concurrent;
using System.Reflection;
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
using Wolverine.AmazonSqs.Internal;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
///     PR #5044 exploratory: how the SQS sender protocol reports broker acceptance to the
///     ServerlessWithOutbox inline send and the durability pass
/// </summary>
public class serverless_with_outbox_sqs_exploratory_5044 : IAsyncLifetime
{
    private const string Schema = "explore5044_sqs";
    private readonly string _queueName = $"explore5044-sqs-{Guid.NewGuid():N}";
    private IHost _host = null!;
    private AmazonSqsQueue _queue = null!;
    private IAmazonSQS _sqs = null!;
    private Explore5044SqsCountingProtocol _counting = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Explore5044SqsHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.UseAmazonSqsTransportLocally().AutoProvision();
                opts.PublishMessage<Explore5044SqsMessage>().ToSqsQueue(_queueName).UseDurableOutbox();
                opts.PublishMessage<Explore5044SqsBigMessage>().ToSqsQueue(_queueName + "-frag")
                    .FragmentOversizedMessages().UseDurableOutbox();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await _host.GetRuntime().Storage.Admin.ClearAllAsync();

        var transport = _host.GetRuntime().Options.AmazonSqsTransport();
        _queue = transport.Queues[_queueName];
        _sqs = transport.Client!;
        _counting = Explore5044SqsCountingProtocol.Wrap(_host, _queue.Uri);
    }

    public async ValueTask DisposeAsync()
    {
        var transport = _host.GetRuntime().Options.AmazonSqsTransport();
        foreach (var queue in transport.Queues.Where(x => x.QueueName.StartsWith("explore5044")))
        {
            try
            {
                await queue.TeardownAsync(NullLogger.Instance);
            }
            catch
            {
                // already gone
            }
        }

        await _host.StopAsync();
        _host.Dispose();
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

    // Drains the queue, deleting what it reads, so a later call only sees what came after
    private async Task<int> drainAsync(string queueName, int expected, TimeSpan wait)
    {
        var url = (await _sqs.GetQueueUrlAsync(queueName, Token)).QueueUrl;
        var count = 0;
        var deadline = DateTimeOffset.UtcNow.Add(wait);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await _sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest(url) { MaxNumberOfMessages = 10, WaitTimeSeconds = 1 }, Token);
            var messages = response.Messages ?? [];
            count += messages.Count;
            foreach (var message in messages) await _sqs.DeleteMessageAsync(url, message.ReceiptHandle, Token);
            if (messages.Count == 0 && count >= expected) break;
        }

        return count;
    }

    [Fact]
    public async Task happy_path_sends_one_protocol_batch_and_leaves_no_outbox_rows()
    {
        await _host.MessageBus().InvokeAsync(new Explore5044SqsTrigger(15, -1), Token);

        // An SQS queue's MessageBatchSize is 10, the SendMessageBatch limit, so two protocol calls
        _queue.MessageBatchSize.ShouldBe(10);
        _counting.Batches.Select(x => x.Length).ToArray().ShouldBe(new[] { 10, 5 });
        (await drainAsync(_queueName, 15, 15.Seconds())).ShouldBe(15);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task an_oversized_message_does_not_hold_back_its_siblings_but_its_row_is_never_discarded()
    {
        // 3 ordinary messages and one over SQS's limit, with no fragmenting
        await _host.MessageBus().InvokeAsync(new Explore5044SqsTrigger(4, 1), Token);

        (await drainAsync(_queueName, 3, 10.Seconds())).ShouldBe(3);
        var rowsAfterInline = await outboxRowsAsync();
        rowsAfterInline.Count.ShouldBe(1);
        rowsAfterInline.Values.Single().ShouldBe(0);

        for (var i = 0; i < 2; i++)
        {
            await makeOutboxStaleAsync();
            await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);
        }

        var rowsAfterPasses = await outboxRowsAsync();

        // EXPECTED, matching a durable sending agent in every other mode, which deletes the outbox row of an
        // envelope the protocol reports as a serialization failure (DurableSendingAgent.MarkSerializationFailureAsync):
        // a message SQS can never take is discarded rather than retried by every pass
        rowsAfterPasses.ShouldBeEmpty(
            $"the oversized row is still in the outbox after the inline send and 2 passes ({_counting.Batches.Count} protocol calls: {string.Join(",", _counting.Batches.Select(x => x.Length))})");
    }

    [Fact]
    public async Task an_oversized_message_on_a_fragmenting_queue_goes_out_with_its_siblings()
    {
        await _host.MessageBus().InvokeAsync(new Explore5044SqsBigTrigger(3), Token);

        // 2 ordinary messages, and one of ~600KB split into fragments
        (await drainAsync(_queueName + "-frag", 4, 15.Seconds())).ShouldBeGreaterThan(3);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_queue_that_no_longer_exists_leaves_every_row_and_a_pass_delivers_once_it_is_back()
    {
        await _sqs.DeleteQueueAsync(_queue.QueueUrl, Token);

        await _host.MessageBus().InvokeAsync(new Explore5044SqsTrigger(3, -1), Token);

        var rows = await outboxRowsAsync();
        rows.Count.ShouldBe(3);
        rows.Values.ShouldAllBe(x => x == 0);

        await _sqs.CreateQueueAsync(_queueName, Token);
        await makeOutboxStaleAsync();
        await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        (await drainAsync(_queueName, 3, 15.Seconds())).ShouldBe(3);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

public record Explore5044SqsTrigger(int Count, int OversizedIndex);

public record Explore5044SqsMessage(int Number, string Payload);

public record Explore5044SqsBigTrigger(int Count);

public record Explore5044SqsBigMessage(int Number, string Payload);

public static class Explore5044SqsHandler
{
    public static OutgoingMessages Handle(Explore5044SqsTrigger trigger)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < trigger.Count; i++)
        {
            messages.Add(new Explore5044SqsMessage(i, i == trigger.OversizedIndex ? new string('x', 400_000) : "small"));
        }

        return messages;
    }

    public static OutgoingMessages Handle(Explore5044SqsBigTrigger trigger)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < trigger.Count; i++)
        {
            messages.Add(new Explore5044SqsBigMessage(i, i == 1 ? new string('x', 600_000) : "small"));
        }

        return messages;
    }
}

internal class Explore5044SqsCountingProtocol : ISenderProtocol
{
    private readonly ISenderProtocol _inner;

    public Explore5044SqsCountingProtocol(ISenderProtocol inner) => _inner = inner;

    public ConcurrentQueue<Envelope[]> Calls { get; } = new();
    public List<Envelope[]> Batches => Calls.ToList();

    public Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        Calls.Enqueue(batch.Messages.ToArray());
        return _inner.SendBatchAsync(callback, batch);
    }

    public static Explore5044SqsCountingProtocol Wrap(IHost host, Uri uri)
    {
        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(uri);
        var sender = (ISender)agent.GetType().GetProperty("Sender")!.GetValue(agent)!;
        var batched = sender.ShouldBeOfType<BatchedSender>();
        var field = typeof(BatchedSender).GetField("_protocol", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counting = new Explore5044SqsCountingProtocol((ISenderProtocol)field.GetValue(batched)!);
        field.SetValue(batched, counting);
        return counting;
    }
}
