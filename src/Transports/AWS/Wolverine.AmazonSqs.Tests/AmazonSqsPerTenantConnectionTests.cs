using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// Integration coverage for broker-per-tenant Amazon SQS (GH-3304). LocalStack is a single container and does not
/// enforce AWS account boundaries, so these tests cannot prove <em>separate accounts</em>. They instead simulate a
/// tenant with its own dedicated connection by pointing the tenant at a different signing region
/// (<c>AuthenticationRegion</c>) on the same LocalStack endpoint — LocalStack partitions SQS queues per region, so
/// the tenant's "colors" queue is genuinely a different physical queue from the shared/default one. That lets us
/// assert real message routing (a tenant message lands on the tenant queue and NOT the shared one, and vice versa)
/// plus inbound <c>Envelope.TenantId</c> stamping, rather than physical broker separation.
///
/// Guarded to skip when LocalStack/Docker is unavailable.
/// </summary>
public class AmazonSqsPerTenantConnectionTests : IAsyncLifetime
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string SharedRegion = "us-east-1";
    private const string TenantRegion = "us-west-2";

    private bool _skip;

    public async Task InitializeAsync()
    {
        _skip = !await IsLocalStackAvailable();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task tenant_message_is_published_to_the_tenant_region_and_not_the_default()
    {
        if (_skip) return;

        var queue = $"pertenant-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(queue);

        await host.MessageBus().SendAsync(new TenantColorMessage("for-tenant-b"),
            new DeliveryOptions { TenantId = "tenantB" });

        // Landed on the tenant region's queue...
        (await consumeOne(TenantRegion, queue, 15.Seconds())).ShouldNotBeNull();
        // ...and NOT on the shared region's queue.
        (await consumeOne(SharedRegion, queue, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task default_message_is_published_to_the_shared_region()
    {
        if (_skip) return;

        var queue = $"pertenant-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(queue);

        await host.MessageBus().SendAsync(new TenantColorMessage("no-tenant"));

        // Falls back to the shared/default region (TenantedIdBehavior.FallbackToDefault)...
        (await consumeOne(SharedRegion, queue, 15.Seconds())).ShouldNotBeNull();
        // ...and NOT the tenant region.
        (await consumeOne(TenantRegion, queue, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task tenant_message_is_consumed_and_stamped_with_the_tenant_id()
    {
        if (_skip) return;

        var queue = $"pertenant-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                configureTransport(opts);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>().ToSqsQueue(queue).SendInline();
                opts.ListenToSqsQueue(queue);
            })
            .StartAsync();

        // The default listener polls the shared region and the tenant listener polls the tenant region; the message
        // only exists in the tenant region, so only the tenant listener consumes it and stamps the tenant id.
        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<TenantColorMessage>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new TenantColorMessage("for-tenant-b"), new DeliveryOptions { TenantId = "tenantB" }));

        var received = session.Received.SingleEnvelope<TenantColorMessage>();
        received.TenantId.ShouldBe("tenantB");
        received.Message.ShouldBeOfType<TenantColorMessage>().Color.ShouldBe("for-tenant-b");
    }

    [Fact]
    public async Task tenant_aware_endpoint_resolves_a_TenantedSender()
    {
        if (_skip) return;

        var queue = $"pertenant-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(queue);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<Wolverine.AmazonSqs.Internal.AmazonSqsTransport>();
        var endpoint = transport.Queues[queue];

        // buildSenderAsync publishes inline, so the endpoint resolves an InlineSendingAgent around our sender.
        var agent = (InlineSendingAgent)runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        agent.Sender.ShouldBeOfType<TenantedSender>();

        // Each tenant got its own compiled child transport with its own client.
        transport.Tenants["tenantB"].Transport.Client.ShouldNotBeNull();
    }

    [Fact]
    public async Task per_tenant_queues_are_provisioned_on_the_tenant_region()
    {
        if (_skip) return;

        var queue = $"pertenant-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(queue);

        // AutoProvision + ConnectAsync must have created the shared topology queue on the tenant's own region.
        using var tenantClient = rawClient(TenantRegion);
        var url = await tenantClient.GetQueueUrlAsync(queue);
        url.QueueUrl.ShouldNotBeNull();
        url.QueueUrl.ShouldContain(TenantRegion);
    }

    private static void configureTransport(WolverineOptions opts)
    {
        opts.UseAmazonSqsTransport(c =>
            {
                c.ServiceURL = ServiceUrl;
                c.AuthenticationRegion = SharedRegion;
            })
            .Credentials(new BasicAWSCredentials("ignore", "ignore"))
            .AutoProvision()
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
            // The tenant shares the LocalStack endpoint but signs for a different region, which LocalStack
            // partitions into its own physical queue store — standing in for a dedicated tenant connection.
            .AddTenant("tenantB", c =>
            {
                c.ServiceURL = ServiceUrl;
                c.AuthenticationRegion = TenantRegion;
            });
    }

    private static Task<IHost> buildSenderAsync(string queue)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantSender";
                configureTransport(opts);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>().ToSqsQueue(queue).SendInline();
            })
            .StartAsync();
    }

    private static AmazonSQSClient rawClient(string region)
    {
        return new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
            new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = region });
    }

    // Read a single message from the given region/queue within the timeout, or null if none arrives.
    private static async Task<Message?> consumeOne(string region, string queueName, TimeSpan timeout)
    {
        using var client = rawClient(region);

        string queueUrl;
        try
        {
            queueUrl = (await client.GetQueueUrlAsync(queueName)).QueueUrl;
        }
        catch (Amazon.SQS.Model.QueueDoesNotExistException)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        do
        {
            var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1
            });

            if (response.Messages is { Count: > 0 })
            {
                return response.Messages[0];
            }
        } while (DateTimeOffset.UtcNow < deadline);

        return null;
    }

    private static async Task<bool> IsLocalStackAvailable()
    {
        try
        {
            using var client = rawClient(SharedRegion);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ListQueuesAsync(new ListQueuesRequest(), cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
