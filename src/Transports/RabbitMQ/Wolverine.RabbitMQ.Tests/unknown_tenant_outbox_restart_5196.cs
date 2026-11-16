using System.Net;
using System.Text.Json;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5196, issue test 4. A message for an unknown tenant that an outbox transaction already wrote is dead lettered
/// first and its outbox row deleted second. Those are two operations, so a node can stop between them. On restart the
/// recovered outbox row must be dead lettered again rather than sent, without a second dead letter, and the row must
/// go away so nothing retries it
/// </summary>
public class unknown_tenant_outbox_restart_5196
{
    private const string VirtualHost = "strict5196outbox";
    private const string SchemaName = "strict5196";

    private static async Task<HttpClient> managementClientAsync()
    {
        var handler = new HttpClientHandler { Credentials = new NetworkCredential("guest", "guest") };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:15672/") };
        await client.PutAsync($"api/vhosts/{VirtualHost}", null);
        return client;
    }

    private static async Task<long> messagesInAsync(HttpClient client, string virtualHost, string queueName)
    {
        var response = await client.GetAsync(
            $"api/queues/{Uri.EscapeDataString(virtualHost)}/{Uri.EscapeDataString(queueName)}");

        // Never declared in that virtual host means nothing was ever sent there
        if (response.StatusCode == HttpStatusCode.NotFound) return 0;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("messages", out var messages) ? messages.GetInt64() : 0;
    }

    private static Task<IHost> startNodeAsync(string queueName)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

                opts.UseRabbitMq().AutoProvision()
                    .AddTenant("one", VirtualHost);

                // Declared without a listener, so the queue depth shows whether anything was sent
                opts.PublishMessage<StrictTenancyIncoming>().ToRabbitQueue(queueName).UseDurableOutbox();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_restart_between_the_dead_letter_and_the_outbox_delete_neither_sends_nor_duplicates()
    {
        using var management = await managementClientAsync();
        var queueName = "strict-outbox-" + Guid.NewGuid().ToString("N");

        // The first node gets as far as writing the dead letter, then stops before it deletes the outbox row
        Envelope envelope;
        using (var first = await startNodeAsync(queueName))
        {
            var runtime = first.GetRuntime();
            await runtime.Storage.Admin.ClearAllAsync();

            envelope = runtime.RoutingFor(typeof(StrictTenancyIncoming))
                .RouteForSend(new StrictTenancyIncoming(Guid.NewGuid()), new DeliveryOptions { TenantId = "bogus" })
                .Single();

            // What an outbox transaction writes before the sending agent sees the message...
            await runtime.Storage.Outbox.StoreOutgoingAsync(envelope, runtime.DurabilitySettings.AssignedNodeNumber);

            // ...and the first half of the agent's work: the dead letter
            await runtime.Storage.Inbox.MoveToDeadLetterStorageAsync(envelope,
                new UnknownBrokerTenantException("bogus", envelope.MessageType, envelope.Destination!));

            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        // The restarted node recovers the outbox row
        using var second = await startNodeAsync(queueName);
        var storage = second.GetRuntime().Storage;

        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());
        while ((await storage.Admin.AllOutgoingAsync()).Any() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        // The row is gone, so nothing will ever retry it
        (await storage.Admin.AllOutgoingAsync()).ShouldBeEmpty();

        // One dead letter, not two
        var deadLetters = await storage.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery([envelope.Id]),
            TestContext.Current.CancellationToken);
        deadLetters.Envelopes.ShouldHaveSingleItem().Envelope.TenantId.ShouldBe("bogus");

        // And no broker got it: not the default virtual host, not the tenant's
        (await messagesInAsync(management, "/", queueName)).ShouldBe(0);
        (await messagesInAsync(management, VirtualHost, queueName)).ShouldBe(0);

        await second.StopAsync(TestContext.Current.CancellationToken);
    }
}
