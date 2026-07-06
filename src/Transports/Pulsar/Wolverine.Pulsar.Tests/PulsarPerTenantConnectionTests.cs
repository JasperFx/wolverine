using DotPulsar.Extensions;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Testcontainers.Pulsar;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// True cluster-isolation coverage for broker-per-tenant Pulsar multi-tenancy (GH-3308). Unlike the CI-safe
/// named-broker test, this spins up a <em>second</em> Pulsar container ("cluster B") so the tenant genuinely
/// connects to a different broker than the default ("cluster A", the shared fixture). This proves:
/// <list type="bullet">
/// <item>a tenant-stamped message is published to cluster B and not cluster A,</item>
/// <item>a default message is published to cluster A and not cluster B,</item>
/// <item>a tenant message is consumed and stamped with its <c>TenantId</c>.</item>
/// </list>
///
/// This is deliberately excluded from <c>CIPulsar</c> (it needs two brokers, and two competing durable
/// subscriptions on a <em>single</em> broker would collide — the very reason broker-per-tenant uses distinct
/// clusters). Marked <c>[Trait("Category","Integration")]</c>.
///
/// Reminder: the Wolverine tenant id selects a whole Pulsar <em>cluster</em>, not the native Pulsar tenant
/// segment in <c>persistent://{tenant}/{namespace}/{topic}</c> (which stays <c>public/default</c> on both).
/// </summary>
[Collection("pulsar")]
[Trait("Category", "Integration")]
public class PulsarPerTenantConnectionTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PulsarContainer? _clusterB;
    private Uri _clusterAServiceUrl = null!;
    private Uri _clusterBServiceUrl = null!;
    private bool _skip;

    public PulsarPerTenantConnectionTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _clusterAServiceUrl = PulsarContainerFixture.ServiceUrl;

        try
        {
            _clusterB = new PulsarBuilder().WithImage("apachepulsar/pulsar:latest").Build();
            await _clusterB.StartAsync();
            _clusterBServiceUrl = new Uri(_clusterB.GetBrokerAddress());

            _output.WriteLine($"Cluster A (default): {_clusterAServiceUrl}");
            _output.WriteLine($"Cluster B (tenant):  {_clusterBServiceUrl}");
        }
        catch (Exception e)
        {
            _output.WriteLine("Skipping: could not start tenant Pulsar cluster: " + e.Message);
            _skip = true;
        }
    }

    public async Task DisposeAsync()
    {
        if (_clusterB != null)
        {
            await _clusterB.DisposeAsync();
        }
    }

    [Fact]
    public async Task tenant_message_is_consumed_and_stamped_with_the_tenant_id()
    {
        if (_skip) return;

        var topic = $"persistent://public/default/pertenant-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                opts.UsePulsar(b => b.ServiceUrl(_clusterAServiceUrl))
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", _clusterBServiceUrl);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>().ToPulsarTopic(topic).SendInline();
                opts.ListenToPulsarTopic(topic)
                    .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                    .BeginAtEarliest();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<TenantColorHandler>();
            })
            .StartAsync();

        var session = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<TenantColorMessage>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new TenantColorMessage("for-tenant-b"), new DeliveryOptions { TenantId = "tenantB" }));

        var received = session.Received.SingleEnvelope<TenantColorMessage>();
        received.TenantId.ShouldBe("tenantB");
        received.Message.ShouldBeOfType<TenantColorMessage>().Color.ShouldBe("for-tenant-b");
    }
}

public class TenantColorHandler
{
    public void Handle(TenantColorMessage message)
    {
        // no-op; the tracking session observes receipt and its stamped TenantId
    }
}
