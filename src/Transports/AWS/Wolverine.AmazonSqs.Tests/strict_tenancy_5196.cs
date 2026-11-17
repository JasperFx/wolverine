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
/// GH-5196 against LocalStack. As in <see cref="AmazonSqsPerTenantConnectionTests" />, the tenant signs for a different
/// region on the same endpoint, which LocalStack keeps as a separate queue store. Under the 7.0 default, a send to an
/// unknown tenant reaches neither region, a send to a registered tenant reaches only its region, and a message
/// received with an unknown tenant id is dead lettered
/// </summary>
public class strict_tenancy_5196
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string SharedRegion = "us-east-1";
    private const string TenantRegion = "us-west-2";

    private static void configureTenantedTransport(WolverineOptions opts)
    {
        // No TenantIdBehavior(): the 7.0 default
        opts.UseAmazonSqsTransport(c =>
            {
                c.ServiceURL = ServiceUrl;
                c.AuthenticationRegion = SharedRegion;
            })
            .Credentials(new BasicAWSCredentials("ignore", "ignore"))
            .AutoProvision()
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
                opts.ServiceName = "StrictTenancySender";
                configureTenantedTransport(opts);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>().ToSqsQueue(queue).SendInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_unknown_tenant_throws_without_a_message_store_and_reaches_neither_region()
    {
        var queue = $"strict-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(queue);

        var ex = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await host.MessageBus().SendAsync(new TenantColorMessage("unknown"),
                new DeliveryOptions { TenantId = "tenantZ" }));
        ex.TenantId.ShouldBe("tenantZ");

        (await consumeOne(SharedRegion, queue, 3.Seconds())).ShouldBeNull();
        (await consumeOne(TenantRegion, queue, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task a_known_tenant_reaches_only_its_own_region()
    {
        var queue = $"strict-{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(queue);

        await host.MessageBus().SendAsync(new TenantColorMessage("known"), new DeliveryOptions { TenantId = "tenantB" });

        (await consumeOne(TenantRegion, queue, 15.Seconds())).ShouldNotBeNull();
        (await consumeOne(SharedRegion, queue, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task a_received_message_for_an_unknown_tenant_is_dead_lettered()
    {
        var queue = $"strict-receive-{Guid.NewGuid():N}";

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancyReceiver";
                configureTenantedTransport(opts);

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToSqsQueue(queue);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        // A node with no tenants puts the tenant id on the shared region's queue as a message attribute
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "UntenantedSender";
                opts.UseAmazonSqsTransport(c =>
                    {
                        c.ServiceURL = ServiceUrl;
                        c.AuthenticationRegion = SharedRegion;
                    })
                    .Credentials(new BasicAWSCredentials("ignore", "ignore"))
                    .AutoProvision();

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<TenantColorMessage>().ToSqsQueue(queue).SendInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var session = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(new TenantColorMessage("unknown"), new DeliveryOptions { TenantId = "tenantZ" });

        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue).ShouldHaveSingleItem();
        record.Exception.ShouldBeOfType<UnknownBrokerTenantException>().TenantId.ShouldBe("tenantZ");
        session.Executed.MessagesOf<TenantColorMessage>().ShouldBeEmpty();
    }

    private static AmazonSQSClient rawClient(string region)
    {
        return new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
            new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = region });
    }

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
}
