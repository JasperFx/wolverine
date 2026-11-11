using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// The SQS MassTransit mapper leaves Envelope.Id to the listener, which derives it from the SQS message, and the
/// durable inbox stores the envelope under that id. The MassTransit messageId is only read later, when the handler
/// pipeline deserializes the body, and that read must not replace the id: marking the message handled finds its
/// inbox row by the id it was stored under.
/// </summary>
public class masstransit_interop_with_durable_inbox_keeps_inbox_id
{
    private const string Schema = "sqs_mt_durable_inbox";

    [Fact]
    public async Task a_handled_masstransit_message_is_marked_handled_in_the_inbox()
    {
        var queue = $"mt-inbox-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.ListenToSqsQueue(queue)
                    .UseMassTransitInterop()
                    .DefaultIncomingMessage<MassTransitInboxOrder>()
                    .UseDurableInbox();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(MassTransitInboxOrderHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var client = new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });
        var queueUrl = (await client.GetQueueUrlAsync(queue, TestContext.Current.CancellationToken)).QueueUrl;

        // What a MassTransit producer puts on the wire: its metadata, and the real message under "message"
        var body = $$"""
            { "messageId": "{{Guid.NewGuid()}}", "messageType": ["urn:message:Orders:MassTransitInboxOrder"],
              "message": { "orderId": 5 } }
            """;
        await client.SendMessageAsync(new SendMessageRequest(queueUrl, body), TestContext.Current.CancellationToken);

        await MassTransitInboxOrderHandler.Handled.WaitAsync(60.Seconds(), TestContext.Current.CancellationToken);

        // The mark-handled write is not part of the handler's own commit, so give it a moment to land
        var statuses = new List<string>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        while (true)
        {
            statuses = await statusesAsync(conn, queue);
            if (statuses.Count == 0 || statuses.All(x => x == "Handled") || DateTimeOffset.UtcNow > deadline) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        // Either retained as Handled or already deleted; never left Incoming
        statuses.ShouldNotContain("Incoming", $"Inbox statuses: {string.Join(", ", statuses)}");
    }

    private static async Task<List<string>> statusesAsync(NpgsqlConnection conn, string queue)
    {
        await using var cmd = new NpgsqlCommand(
            $"select status from {Schema}.wolverine_incoming_envelopes where received_at like @uri", conn);
        cmd.Parameters.AddWithValue("uri", $"%{queue}%");
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) result.Add(reader.GetString(0));
        return result;
    }
}

public record MassTransitInboxOrder(int OrderId);

public static class MassTransitInboxOrderHandler
{
    private static readonly TaskCompletionSource _handled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task Handled => _handled.Task;

    public static void Handle(MassTransitInboxOrder message) => _handled.TrySetResult();
}
