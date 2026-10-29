using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Http.Tests.Transport;

/// <summary>
///     GH-5276. The batch endpoint of the HTTP transport answered 200 whenever the durable receiver returned
///     normally -- including when the durable receiver could not write the batch to the inbox. The sender then
///     deleted the envelopes from its outbox, while the receiver "deferred" them back to the HTTP transport's
///     NulloListener, which does nothing. The messages ended up in no table on either side
/// </summary>
public class batch_inbox_write_failure_5276
{
    private const string Schema = "http_batch_5276";

    private const string BatchUrl = "http://localhost/_wolverine/batch/incoming";

    private static async Task<IAlbaHost> hostWith(Action<WolverineOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(BatchedPingHandler));

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);
            opts.Services.AddResourceSetupOnStartup();

            opts.LocalQueue("incoming").UseDurableInbox();

            configure?.Invoke(opts);
        });

        builder.Services.AddWolverineHttp();

        // The HTTP transport's sender posts to this same host through the test server
        builder.Services.AddHttpClient(BatchUrl, c => c.BaseAddress = new Uri(BatchUrl))
            .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler());

        var host = await AlbaHost.For(builder, app => app.MapWolverineHttpTransportEndpoints());
        await host.ResetResourceState(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<HttpStatusCode> postBatch(HttpClient client, string name)
    {
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        var data = EnvelopeSerializer.Serialize(new Envelope[]
        {
            new(new BatchedPing(name)) { Serializer = serializer }
        });

        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeBatchContentType);

        var response = await client.PostAsync("/_wolverine/batch/incoming", content,
            TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static Task renameInboxTable() =>
        sql($"alter table {Schema}.wolverine_incoming_envelopes rename to wolverine_incoming_envelopes_aside");

    private static Task restoreInboxTable() =>
        sql($"alter table {Schema}.wolverine_incoming_envelopes_aside rename to wolverine_incoming_envelopes");

    private static async Task sql(string statement)
    {
        await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
        return condition();
    }

    [Fact]
    public async Task a_batch_the_inbox_could_not_store_is_not_answered_with_success()
    {
        await using var host = await hostWith();
        var client = host.Server.CreateClient();

        // Control: with the inbox healthy the batch is accepted and handled
        (await postBatch(client, "control")).ShouldBe(HttpStatusCode.OK);
        (await eventually(() => BatchedPingHandler.Handled.Contains("control"), 15.Seconds())).ShouldBeTrue();

        await renameInboxTable();

        HttpStatusCode status;
        try
        {
            status = await postBatch(client, "during-outage");

            // Let the durable receiver's RetryBlock run out while the table is still gone
            await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);
        }
        finally
        {
            await restoreInboxTable();
        }

        var handled = await eventually(() => BatchedPingHandler.Handled.Contains("during-outage"), 15.Seconds());
        // Either the receiver refused the batch, so the sender keeps it in its outbox and retries it,
        // or the receiver kept it and handled it. Answering 200 and then dropping it is the bug.
        if (status == HttpStatusCode.OK)
        {
            var incoming = await host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.AllIncomingAsync();
            handled.ShouldBeTrue(
                $"The batch was answered 200 but the message was never handled, and it is not in the receiver's inbox either ({incoming.Count} row(s), all from the control message)");
        }
        else
        {
            ((int)status).ShouldBeGreaterThanOrEqualTo(500);
        }
    }

    [Fact]
    public async Task a_durable_outbox_retries_a_batch_the_receiver_could_not_store_until_it_is_handled()
    {
        // The shape from the issue: a durable outbox posting to the batch endpoint of a durable local queue
        await using var host = await hostWith(opts =>
            opts.PublishMessage<BatchedPing>().ToHttpEndpoint(BatchUrl).UseDurableOutbox());

        await host.MessageBus().PublishAsync(new BatchedPing("outbox-control"));
        (await eventually(() => BatchedPingHandler.Handled.Contains("outbox-control"), 15.Seconds())).ShouldBeTrue();

        await renameInboxTable();
        try
        {
            await host.MessageBus().PublishAsync(new BatchedPing("outbox-during-outage"));
            await Task.Delay(5.Seconds(), TestContext.Current.CancellationToken);
        }
        finally
        {
            await restoreInboxTable();
        }

        (await eventually(() => BatchedPingHandler.Handled.Contains("outbox-during-outage"), 60.Seconds()))
            .ShouldBeTrue("The message published while the receiver's inbox was down was never handled");
    }
}

public record BatchedPing(string Name);

public static class BatchedPingHandler
{
    public static readonly ConcurrentQueue<string> Handled = new();

    public static void Handle(BatchedPing ping) => Handled.Enqueue(ping.Name);
}
