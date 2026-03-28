using System.Net;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// Reproduces https://github.com/JasperFx/wolverine/issues/2361
/// When using multi-tenant RabbitMQ with durable outbox, messages sent to
/// a non-default broker get stuck in the outbox and are re-delivered every
/// 5 seconds by the durability agent, causing duplicate envelope exceptions.
///
/// Root cause: TenantedSender implemented ISenderRequiresCallback, which caused
/// SendingAgent to use sendWithCallbackHandlingAsync (assumes the inner sender
/// calls back on success). But RabbitMqSender does NOT implement ISenderRequiresCallback,
/// so MarkSuccessfulAsync was never called and the outbox entry was never deleted.
/// </summary>
public class Bug_2361_outbox_stuck_with_tenanted_broker
{
    private readonly ITestOutputHelper _output;

    public Bug_2361_outbox_stuck_with_tenanted_broker(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task messages_sent_to_tenanted_broker_should_be_removed_from_outbox()
    {
        // Create a virtual host for the tenant
        await declareVirtualHost("bug2361");

        var queueName = $"bug2361_{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Bug2361Sender";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "bug2361");

                opts.Services.AddResourceSetupOnStartup();

                // Set up multi-tenant RabbitMQ: default + "tenant1" on a different virtual host
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .DisableDeadLetterQueueing()
                    .AddTenant("tenant1", "bug2361")
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault);

                opts.Policies.DisableConventionalLocalRouting();

                // Publish to a specific queue with durable outbox
                opts.PublishAllMessages()
                    .ToRabbitQueue(queueName)
                    .UseDurableOutbox();

                // Listen on the tenant's queue
                opts.ListenToRabbitQueue(queueName);
            }).StartAsync();

        // Clean up any stale outbox data from previous runs
        await host.ResetResourceState();

        // Send a message targeted at the tenant
        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async bus =>
            {
                await bus.PublishAsync(new Bug2361Message("Hello from tenant"),
                    new DeliveryOptions { TenantId = "tenant1" });
            }));

        // The message should have been received
        session.Received.SingleMessage<Bug2361Message>()
            .ShouldNotBeNull();

        // Wait for async outbox cleanup to complete
        await Task.Delay(3.Seconds());

        // Verify the outbox is empty - no stuck messages
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM bug2361.wolverine_outgoing_envelopes";
        var stuckCount = (long)(await cmd.ExecuteScalarAsync())!;

        _output.WriteLine($"Outbox messages remaining: {stuckCount}");
        stuckCount.ShouldBe(0, "Messages should not be stuck in the outbox after successful send to tenanted broker");
    }

    private static async Task declareVirtualHost(string vhname)
    {
        var credentials = new NetworkCredential("guest", "guest");
        using var handler = new HttpClientHandler { Credentials = credentials };
        using var client = new HttpClient(handler);

        var request = new HttpRequestMessage(HttpMethod.Put, $"http://localhost:15672/api/vhosts/{vhname}");
        await client.SendAsync(request);

        // Grant permissions
        var permRequest = new HttpRequestMessage(HttpMethod.Put,
            $"http://localhost:15672/api/permissions/{vhname}/guest");
        permRequest.Content = new StringContent(
            """{"configure":".*","write":".*","read":".*"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        await client.SendAsync(permRequest);
    }
}

public record Bug2361Message(string Text);

public static class Bug2361MessageHandler
{
    public static void Handle(Bug2361Message message)
    {
        // Simple handler - just receives the message
    }
}
