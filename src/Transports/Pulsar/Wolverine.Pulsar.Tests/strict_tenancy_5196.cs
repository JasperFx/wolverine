using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Testcontainers.Pulsar;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// GH-5196 against two real Pulsar clusters: A is the shared connection, B the tenant's own. Under the 7.0 default,
/// a send to an unknown tenant reaches neither cluster, a send to a registered tenant reaches only that tenant's
/// cluster, and a message received with an unknown tenant id is dead lettered
/// </summary>
[Collection("pulsar")]
[Trait("Category", "Integration")]
public class strict_tenancy_5196 : IAsyncLifetime
{
    private PulsarContainer _clusterB = null!;
    private Uri _clusterAServiceUrl = null!;
    private Uri _clusterBServiceUrl = null!;

    public async ValueTask InitializeAsync()
    {
        _clusterAServiceUrl = PulsarContainerFixture.ServiceUrl;

        _clusterB = new PulsarBuilder().WithImage(PulsarContainerFixture.PulsarImage).Build();
        await _clusterB.StartAsync();
        _clusterBServiceUrl = new Uri(_clusterB.GetBrokerAddress());
    }

    public async ValueTask DisposeAsync()
    {
        await _clusterB.DisposeAsync();
    }

    private Task<IHost> buildTenantedHostAsync(string topic)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancy";

                // No TenantIdBehavior(): the 7.0 default
                opts.UsePulsar(b => b.ServiceUrl(_clusterAServiceUrl))
                    .AddTenant("tenantB", _clusterBServiceUrl);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>().ToPulsarTopic(topic);
                opts.ListenToPulsarTopic(topic)
                    .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                    .BeginAtEarliest();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<TenantColorHandler>();
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_unknown_tenant_reaches_neither_cluster_while_known_and_untenanted_messages_go_where_they_belong()
    {
        var topic = $"persistent://public/default/strict-{Guid.NewGuid():N}";
        using var host = await buildTenantedHostAsync(topic);

        UnknownBrokerTenantException? unknown = null;

        // The listener consumes both clusters. The unknown tenant goes first; the two later messages are the proof
        // that nothing for it ever arrived on either cluster
        var session = await host.TrackActivity()
            .Timeout(120.Seconds())
            .WaitForCondition(new ReceivedCount(2))
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                unknown = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
                    await c.SendAsync(new TenantColorMessage("unknown"), new DeliveryOptions { TenantId = "tenantZ" }));

                await c.SendAsync(new TenantColorMessage("known"), new DeliveryOptions { TenantId = "tenantB" });
                await c.SendAsync(new TenantColorMessage("untenanted"));
            }));

        unknown.ShouldNotBeNull().TenantId.ShouldBe("tenantZ");

        // Give a wrongly sent message a moment longer to show up behind the other two
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        var received = session.Received.Envelopes().Where(x => x.Message is TenantColorMessage).ToArray();
        received.Select(x => ((TenantColorMessage)x.Message!).Color).OrderBy(x => x)
            .ShouldBe(["known", "untenanted"]);
        received.Single(x => ((TenantColorMessage)x.Message!).Color == "known").TenantId.ShouldBe("tenantB");
    }

    [Fact]
    public async Task a_received_message_for_an_unknown_tenant_is_dead_lettered()
    {
        var topic = $"persistent://public/default/strict-receive-{Guid.NewGuid():N}";
        using var receiver = await buildTenantedHostAsync(topic);

        // A node with no tenants puts the tenant id on cluster A as a property
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "UntenantedSender";
                opts.UsePulsar(b => b.ServiceUrl(_clusterAServiceUrl));
                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<TenantColorMessage>().ToPulsarTopic(topic);
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

    private class ReceivedCount(int expected) : ITrackedCondition
    {
        private int _count;

        public void Record(EnvelopeRecord record)
        {
            if (record.MessageEventType == MessageEventType.Received && record.Message is TenantColorMessage)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public bool IsCompleted() => Volatile.Read(ref _count) >= expected;
    }
}
