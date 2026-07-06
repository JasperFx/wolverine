using DotPulsar.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// Registration-level coverage for broker-per-tenant Pulsar multi-tenancy (GH-3308). No running broker is
/// required (DotPulsar producers/consumers connect lazily), so these run in CI.
///
/// Reminder: the Wolverine tenant id here selects a whole Pulsar <em>cluster</em> (service URL + auth), not the
/// native Pulsar tenant segment in <c>persistent://{tenant}/{namespace}/{topic}</c>.
/// </summary>
public class PulsarPerTenantConfigurationTests
{
    [Fact]
    public void add_tenant_registers_a_tenant_with_its_own_transport()
    {
        var options = new WolverineOptions();
        var pulsar = options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://shared:6650")));
        pulsar.AddTenant("tenantB", new Uri("pulsar://tenant-b:6650"));

        var transport = pulsar.Transport;
        transport.Tenants.Count().ShouldBe(1);

        var tenant = transport.Tenants["tenantB"];
        tenant.TenantId.ShouldBe("tenantB");
        tenant.Transport.ShouldNotBeSameAs(transport);
    }

    [Fact]
    public void add_tenant_action_overload_registers_the_tenant()
    {
        var options = new WolverineOptions();
        var pulsar = options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://shared:6650")));
        pulsar.AddTenant("tenantB", b => b.ServiceUrl(new Uri("pulsar://tenant-b:6650")));

        pulsar.Transport.Tenants["tenantB"].TenantId.ShouldBe("tenantB");
    }

    [Fact]
    public void compile_copies_parent_dead_letter_and_retry_defaults_onto_the_tenant()
    {
        var options = new WolverineOptions();
        var pulsar = options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://shared:6650")));
        pulsar.DeadLetterQueueing(DeadLetterTopic.DefaultNative);
        pulsar.RetryLetterQueueing(new RetryLetterTopic([TimeSpan.FromSeconds(1)]));
        pulsar.AddTenant("tenantB", new Uri("pulsar://tenant-b:6650"));

        var transport = pulsar.Transport;
        var tenant = transport.Tenants["tenantB"];
        tenant.Compile(transport);

        tenant.Transport.DeadLetterTopic.ShouldBeSameAs(transport.DeadLetterTopic);
        tenant.Transport.RetryLetterTopic.ShouldBeSameAs(transport.RetryLetterTopic);
    }

    [Fact]
    public void tenant_id_behavior_setter_flows_to_the_transport()
    {
        var options = new WolverineOptions();
        var pulsar = options.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://shared:6650")));
        pulsar.TenantIdBehavior(TenantedIdBehavior.TenantIdRequired);

        pulsar.Transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.TenantIdRequired);
    }

    [Fact]
    public void pulsar_endpoints_are_tenant_aware_by_default()
    {
        var transport = new PulsarTransport();
        var endpoint = transport.EndpointFor("persistent://public/default/orders");
        endpoint.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);
    }

    [Fact]
    public async Task tenant_aware_endpoint_resolves_a_TenantedSender()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePulsar(b => b.ServiceUrl(new Uri("pulsar://localhost:6650")))
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenantB", new Uri("pulsar://localhost:6660"));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColorMessage>()
                    .ToPulsarTopic("persistent://public/default/tenant-colors");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<PulsarTransport>();
        var endpoint = transport.EndpointFor("persistent://public/default/tenant-colors");

        var agent = (SendingAgent)runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        agent.Sender.ShouldBeOfType<TenantedSender>();
    }
}

public record TenantColorMessage(string Color);
