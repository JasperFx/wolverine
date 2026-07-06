using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// Integration coverage for broker-per-tenant Google Cloud Platform Pub/Sub (GH-3306). Project-id-per-tenant is the
/// isolation axis: the default connection uses project "wolverine" and the tenant uses project "wolverine2". The
/// Pub/Sub emulator accepts arbitrary project ids at request time on the same endpoint, so the tenant's topic/
/// subscription are genuinely distinct GCP resources from the shared/default ones — letting us prove real routing
/// (a tenant message lands under the tenant project and NOT the default one, and vice versa) plus inbound
/// <see cref="Envelope.TenantId"/> stamping.
///
/// Skip-guarded when the emulator/Docker is unavailable.
/// </summary>
public class PubsubPerTenantBrokerTests : IAsyncLifetime
{
    private const string DefaultProject = "wolverine";
    private const string TenantProject = "wolverine2";
    private const string TenantId = "tenant2";

    private bool _skip;

    public async Task InitializeAsync()
    {
        _skip = !await TestingExtensions.IsEmulatorAvailable();
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", TestingExtensions.EmulatorHost);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task tenant_message_is_published_to_the_tenant_project_and_not_the_default()
    {
        if (_skip) return;

        var topic = $"pertenant-{Guid.NewGuid():N}";

        // Pre-create a topic + verification subscription under BOTH projects. The subscription must exist before we
        // publish for Pub/Sub to retain the message.
        var defaultSub = await provisionAsync(DefaultProject, topic);
        var tenantSub = await provisionAsync(TenantProject, topic);

        using var host = await buildSenderHostAsync(topic);

        await host.MessageBus().SendAsync(new PerTenantMessage("for-tenant"),
            new DeliveryOptions { TenantId = TenantId });

        // Landed under the tenant project...
        (await pullOneAsync(tenantSub, 15.Seconds())).ShouldNotBeNull();
        // ...and NOT under the default project.
        (await pullOneAsync(defaultSub, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task default_message_is_published_to_the_default_project()
    {
        if (_skip) return;

        var topic = $"pertenant-{Guid.NewGuid():N}";

        var defaultSub = await provisionAsync(DefaultProject, topic);
        var tenantSub = await provisionAsync(TenantProject, topic);

        using var host = await buildSenderHostAsync(topic);

        // No tenant id => FallbackToDefault routes to the shared/default connection.
        await host.MessageBus().SendAsync(new PerTenantMessage("no-tenant"));

        (await pullOneAsync(defaultSub, 15.Seconds())).ShouldNotBeNull();
        (await pullOneAsync(tenantSub, 3.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task tenant_message_is_consumed_and_stamped_with_the_tenant_id()
    {
        if (_skip) return;

        var topic = $"pertenant-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .TenantIdBehavior(Transports.Sending.TenantedIdBehavior.FallbackToDefault)
                    .AddTenant(TenantId, TenantProject,
                        t => t.EmulatorDetection = EmulatorDetection.EmulatorOnly);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<PerTenantMessage>().ToPubsubTopic(topic).SendInline();
                opts.ListenToPubsubTopic(topic);
            })
            .StartAsync();

        // The default listener polls the default project and the tenant listener polls the tenant project; the
        // message only exists under the tenant project, so only the tenant listener consumes it and stamps the id.
        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<PerTenantMessage>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new PerTenantMessage("for-tenant"), new DeliveryOptions { TenantId = TenantId }));

        var received = session.Received.SingleEnvelope<PerTenantMessage>();
        received.TenantId.ShouldBe(TenantId);
        received.Message.ShouldBeOfType<PerTenantMessage>().Value.ShouldBe("for-tenant");
    }

    [Fact]
    public async Task auto_provision_creates_the_topology_under_the_tenant_project()
    {
        if (_skip) return;

        var topic = $"pertenant-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantProvisioning";
                opts.UsePubsubTesting()
                    .AutoProvision()
                    .AddTenant(TenantId, TenantProject,
                        t => t.EmulatorDetection = EmulatorDetection.EmulatorOnly);

                opts.PublishMessage<PerTenantMessage>().ToPubsubTopic(topic);
                opts.ListenToPubsubTopic(topic);
            })
            .StartAsync();

        // Prove the topic was actually created under the tenant project (GetTopicAsync throws when absent).
        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();

        var tenantTopic = await publisher.GetTopicAsync(new TopicName(TenantProject, topic));
        tenantTopic.ShouldNotBeNull();
    }

    private static Task<IHost> buildSenderHostAsync(string topic)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantSender";
                opts.UsePubsubTesting()
                    .TenantIdBehavior(Transports.Sending.TenantedIdBehavior.FallbackToDefault)
                    .AddTenant(TenantId, TenantProject,
                        t => t.EmulatorDetection = EmulatorDetection.EmulatorOnly);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<PerTenantMessage>().ToPubsubTopic(topic).SendInline();
            })
            .StartAsync();
    }

    private static async Task<SubscriptionName> provisionAsync(string projectId, string topic)
    {
        var publisher = await new PublisherServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();
        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();

        var topicName = new TopicName(projectId, topic);
        var subscriptionName = new SubscriptionName(projectId, $"{topic}-verify");

        try
        {
            await publisher.CreateTopicAsync(topicName);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }

        try
        {
            await subscriber.CreateSubscriptionAsync(subscriptionName, topicName, pushConfig: null,
                ackDeadlineSeconds: 60);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
        }

        return subscriptionName;
    }

    private static async Task<ReceivedMessage?> pullOneAsync(SubscriptionName subscription, TimeSpan timeout)
    {
        var subscriber = await new SubscriberServiceApiClientBuilder
        {
            EmulatorDetection = EmulatorDetection.EmulatorOnly
        }.BuildAsync();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await subscriber.PullAsync(subscription, maxMessages: 1,
                CallSettings.FromExpiration(Expiration.FromTimeout(2.Seconds())));

            var message = response.ReceivedMessages.FirstOrDefault();
            if (message is not null)
            {
                await subscriber.AcknowledgeAsync(subscription, new[] { message.AckId });
                return message;
            }

            await Task.Delay(250.Milliseconds());
        }

        return null;
    }
}

public record PerTenantMessage(string Value);

public static class PerTenantMessageHandler
{
    public static void Handle(PerTenantMessage message)
    {
        // no-op; presence lets Wolverine discover a handler so receive tests can track processing
    }
}
