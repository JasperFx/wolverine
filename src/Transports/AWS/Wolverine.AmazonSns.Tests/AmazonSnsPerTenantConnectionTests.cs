using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
/// Integration coverage for broker-per-tenant Amazon SNS (GH-3305). SNS is publish-only, so this proves the
/// tenant-specific PUBLISHER: a message stamped with a tenant id is published to the tenant's own SNS connection
/// and NOT the shared/default one, and a default message goes to the shared connection.
///
/// LocalStack is a single container and does not enforce AWS account boundaries, so these tests cannot prove
/// <em>separate accounts</em>. They instead simulate a tenant with its own dedicated connection by pointing the
/// tenant at a different signing region (<c>AuthenticationRegion</c>) on the same LocalStack endpoint — LocalStack
/// partitions SNS topics (and SQS queues) per region, so the tenant's "colors" topic is genuinely a different
/// physical topic from the shared/default one. We observe delivery by raw-subscribing an SQS queue to the topic in
/// each region and reading it directly.
///
/// Full per-tenant CONSUMPTION (tenant SNS topic -> tenant SQS queue -> Wolverine listener) composes with the
/// Amazon SQS broker-per-tenant support (PR #3316) and is intentionally out of scope here.
///
/// Guarded to skip when LocalStack/Docker is unavailable.
/// </summary>
public class AmazonSnsPerTenantConnectionTests : IAsyncLifetime
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string SharedRegion = "us-east-1";
    private const string TenantRegion = "us-west-2";

    private bool _skip;

    public async Task InitializeAsync()
    {
        _skip = !await SnsTestingExtensions.IsLocalStackAvailable();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // NOTE on the test design: these two routing tests each provision a subscription observation in only ONE region.
    // We deliberately do NOT stand up same-named observation topics in BOTH regions at once and assert the negative
    // ("and NOT the other region"): LocalStack's community SNS implementation delivers unreliably when a topic of the
    // same name exists in two regions simultaneously (a single-region observation delivers deterministically; a
    // dual-region one drops). SQS partitioning is solid, but the SNS->SQS fan-out across same-named cross-region
    // topics is not. Instead, the pair below proves routing DIVERGES by tenant: the SAME shared topology publishes a
    // tenant message to the TENANT region and a default message to the SHARED region, selected solely by
    // Envelope.TenantId. The tenant-vs-default sender wiring itself is additionally asserted (without a broker) in
    // tenant_aware_topic_resolves_a_TenantedSender and in AmazonSnsPerTenantConfigurationTests.

    [Fact]
    public async Task tenant_message_is_published_to_the_tenant_region()
    {
        if (_skip) return;

        var topic = $"snspertenant-{Guid.NewGuid():N}";
        var queue = $"snspertenant-{Guid.NewGuid():N}";

        var tenantQueueUrl = await provisionObservationAsync(TenantRegion, topic, queue);

        using var host = await buildSenderAsync(topic);

        await host.MessageBus().SendAsync(new TenantColorMessage("for-tenant-b"),
            new DeliveryOptions { TenantId = "tenantB" });

        // The tenant's dedicated connection (tenant region) published to the tenant-region topic -> tenant queue.
        (await consumeOne(TenantRegion, tenantQueueUrl, 20.Seconds())).ShouldNotBeNull();
    }

    [Fact]
    public async Task default_message_is_published_to_the_shared_region()
    {
        if (_skip) return;

        var topic = $"snspertenant-{Guid.NewGuid():N}";
        var queue = $"snspertenant-{Guid.NewGuid():N}";

        var sharedQueueUrl = await provisionObservationAsync(SharedRegion, topic, queue);

        using var host = await buildSenderAsync(topic);

        // No tenant id -> FallbackToDefault -> shared/default connection (shared region).
        await host.MessageBus().SendAsync(new TenantColorMessage("no-tenant"));

        (await consumeOne(SharedRegion, sharedQueueUrl, 20.Seconds())).ShouldNotBeNull();
    }

    [Fact]
    public async Task tenant_aware_topic_resolves_a_TenantedSender()
    {
        if (_skip) return;

        var topic = $"snspertenant-{Guid.NewGuid():N}";
        await provisionObservationAsync(SharedRegion, topic, $"snspertenant-{Guid.NewGuid():N}");
        await provisionObservationAsync(TenantRegion, topic, $"snspertenant-{Guid.NewGuid():N}");

        using var host = await buildSenderAsync(topic);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<AmazonSnsTransport>();
        var endpoint = transport.Topics[topic];

        // buildSenderAsync publishes inline, so the endpoint resolves an InlineSendingAgent around our sender.
        var agent = (InlineSendingAgent)runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        agent.Sender.ShouldBeOfType<TenantedSender>();

        // Each tenant got its own compiled child transport with its own SNS + paired SQS clients.
        transport.Tenants["tenantB"].Transport.SnsClient.ShouldNotBeNull();
        transport.Tenants["tenantB"].Transport.SqsClient.ShouldNotBeNull();
    }

    private static void configureTransport(WolverineOptions opts)
    {
        opts.UseAmazonSnsTransport((sns, sqs) =>
            {
                sns.ServiceURL = ServiceUrl;
                sns.AuthenticationRegion = SharedRegion;
                sqs.ServiceURL = ServiceUrl;
                sqs.AuthenticationRegion = SharedRegion;
            })
            .Credentials(new BasicAWSCredentials("ignore", "ignore"))
            .AutoProvision()
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
            // The tenant shares the LocalStack endpoint but signs for a different region, which LocalStack
            // partitions into its own physical topic store — standing in for a dedicated tenant connection.
            .AddTenant("tenantB", c =>
            {
                c.ServiceURL = ServiceUrl;
                c.AuthenticationRegion = TenantRegion;
            });
    }

    private static Task<IHost> buildSenderAsync(string topic)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "SnsPerTenantSender";
                configureTransport(opts);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>().ToSnsTopic(topic).SendInline();
            })
            .StartAsync();
    }

    private static AmazonSimpleNotificationServiceClient rawSns(string region)
    {
        return new AmazonSimpleNotificationServiceClient(new BasicAWSCredentials("ignore", "ignore"),
            new AmazonSimpleNotificationServiceConfig { ServiceURL = ServiceUrl, AuthenticationRegion = region });
    }

    private static AmazonSQSClient rawSqs(string region)
    {
        return new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
            new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = region });
    }

    // Create the topic + an SQS queue subscribed to it (raw message delivery) in the given region, and return the
    // queue url so the test can read what actually got published to the region's topic.
    private static async Task<string> provisionObservationAsync(string region, string topicName, string queueName)
    {
        using var sns = rawSns(region);
        using var sqs = rawSqs(region);

        var topicArn = (await sns.CreateTopicAsync(topicName)).TopicArn;
        var queueUrl = (await sqs.CreateQueueAsync(queueName)).QueueUrl;

        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = [QueueAttributeName.QueueArn]
        })).QueueARN;

        var policy = $$"""
                       {
                         "Version": "2012-10-17",
                         "Statement": [{
                             "Effect": "Allow",
                             "Principal": { "Service": "sns.amazonaws.com" },
                             "Action": "sqs:SendMessage",
                             "Resource": "{{queueArn}}",
                             "Condition": { "ArnEquals": { "aws:SourceArn": "{{topicArn}}" } }
                         }]
                       }
                       """;

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string> { { "Policy", policy } }
        });

        var subscribe = new SubscribeRequest(topicArn, "sqs", queueArn);
        subscribe.Attributes = new Dictionary<string, string> { { "RawMessageDelivery", "true" } };
        await sns.SubscribeAsync(subscribe);

        return queueUrl;
    }

    private static async Task<Message?> consumeOne(string region, string queueUrl, TimeSpan timeout)
    {
        using var client = rawSqs(region);

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

public record TenantColorMessage(string Color);

public static class TenantColorMessageHandler
{
    public static void Handle(TenantColorMessage message)
    {
        // no-op; presence lets Wolverine discover a handler
    }
}
