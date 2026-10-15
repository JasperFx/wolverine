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
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
///     GH-5047. SQS can never take a message over its size limit from a queue that does not fragment, and its sender
///     protocol says so as a serialization failure. Under ServerlessWithOutbox the inline send and the durability
///     pass move that message to dead letter storage instead of leaving its outbox row for every pass to send again
/// </summary>
public class serverless_with_outbox_oversized_sqs_message_5047 : IAsyncLifetime
{
    private const string Schema = "gh5050_sqs";
    private readonly string _queueName = $"gh5047-sqs-{Guid.NewGuid():N}";
    private IHost _host = null!;
    private IAmazonSQS _sqs = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5047SqsHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.UseAmazonSqsTransportLocally().AutoProvision();

                // No FragmentOversizedMessages(), so a message over 256 KB can never be sent
                opts.PublishMessage<Gh5047SqsMessage>().ToSqsQueue(_queueName).UseDurableOutbox();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
        _sqs = _host.GetRuntime().Options.AmazonSqsTransport().Client!;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _host.GetRuntime().Options.AmazonSqsTransport().Queues[_queueName].TeardownAsync(NullLogger.Instance);
        }
        catch
        {
            // already gone
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<long> countAsync(string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {Schema}.{table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Token));
    }

    private async Task<int> drainAsync(int expected)
    {
        var url = (await _sqs.GetQueueUrlAsync(_queueName, Token)).QueueUrl;
        var count = 0;
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
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
    public async Task the_inline_send_dead_letters_an_oversized_message_and_sends_its_siblings()
    {
        // 3 ordinary messages and one over SQS's limit
        await _host.MessageBus().InvokeAsync(new Gh5047SqsTrigger(4, 1), Token);

        (await drainAsync(3)).ShouldBe(3);

        // Before GH-5047 its row stayed in the outbox, and every durability pass sent it and failed again
        (await countAsync(DatabaseConstants.OutgoingTable)).ShouldBe(0);
        (await countAsync(DatabaseConstants.DeadLetterTable)).ShouldBe(1);
    }

    [Fact]
    public async Task the_durability_pass_dead_letters_an_oversized_message()
    {
        var runtime = _host.GetRuntime();
        var envelope = new Envelope(new Gh5047SqsMessage(0, new string('x', 400_000)))
        {
            Destination = runtime.Options.AmazonSqsTransport().Queues[_queueName].Uri,
            ContentType = EnvelopeConstants.JsonContentType,
            MessageType = typeof(Gh5047SqsMessage).ToMessageTypeName()
        };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);

        // As a request that died before it sent it leaves it, and old enough for a pass to take
        await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, 0);
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"update {Schema}.{DatabaseConstants.OutgoingTable} set \"{DatabaseConstants.Timestamp}\" = now() - interval '2 hours'";
            await cmd.ExecuteNonQueryAsync(Token);
        }

        var report = await _host.RunDurabilityPassAsync(maxDuration: 30.Seconds(), token: Token);

        report.OutgoingDeadLettered.ShouldBe(1);
        report.OutgoingFailed.ShouldBe(0);
        (await countAsync(DatabaseConstants.OutgoingTable)).ShouldBe(0);
        (await countAsync(DatabaseConstants.DeadLetterTable)).ShouldBe(1);
    }
}

public record Gh5047SqsTrigger(int Count, int OversizedIndex);

public record Gh5047SqsMessage(int Number, string Payload);

public static class Gh5047SqsHandler
{
    public static OutgoingMessages Handle(Gh5047SqsTrigger trigger)
    {
        var messages = new OutgoingMessages();
        for (var i = 0; i < trigger.Count; i++)
        {
            messages.Add(new Gh5047SqsMessage(i, i == trigger.OversizedIndex ? new string('x', 400_000) : "small"));
        }

        return messages;
    }
}
