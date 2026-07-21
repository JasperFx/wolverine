using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.MQTT.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.MQTT.Tests;

/// <summary>
/// Integration coverage for broker-per-tenant MQTT (GH-3307). Two in-process <see cref="LocalMqttBroker"/>
/// instances stand in for two brokers on two free ports — broker A is the default/shared connection and broker B
/// is tenant "tenantB"'s own dedicated connection. Because MQTT tenants are always own-connection (no
/// topic-prefix equivalent), isolation is purely by which broker the tenant's client is connected to. Proves:
/// <list type="bullet">
/// <item>a tenant message lands on the tenant's broker (B) and NOT the shared broker (A),</item>
/// <item>a default/untenanted message falls back to the shared broker (A) and NOT the tenant broker (B),</item>
/// <item>an inbound tenant message is consumed and stamped with its <c>TenantId</c>, and</item>
/// <item>each tenant connection is given a unique ClientId so the broker never kicks it.</item>
/// </list>
/// </summary>
[Collection("acceptance")]
public class mqtt_per_tenant_broker_tests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private LocalMqttBroker _brokerA = null!;
    private LocalMqttBroker _brokerB = null!;
    private int _portA;
    private int _portB;

    public mqtt_per_tenant_broker_tests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _portA = PortFinder.GetAvailablePort();
        _portB = PortFinder.GetAvailablePort();

        _brokerA = new LocalMqttBroker(_portA) { Logger = new XUnitLogger(_output, "MQTT-A") };
        _brokerB = new LocalMqttBroker(_portB) { Logger = new XUnitLogger(_output, "MQTT-B") };

        await _brokerA.StartAsync();
        await _brokerB.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _brokerA.DisposeAsync();
        await _brokerB.DisposeAsync();
    }

    [Fact]
    public async Task tenant_message_is_published_to_the_tenant_broker_and_not_the_default()
    {
        var topic = $"tenant/{Guid.NewGuid():N}";

        await using var subOnB = await RawMqttSubscriber.StartAsync(_portB, topic);
        await using var subOnA = await RawMqttSubscriber.StartAsync(_portA, topic);

        using var host = await buildSenderAsync(topic);

        await host.MessageBus().SendAsync(new TenantColor("for-tenant-b"),
            new DeliveryOptions { TenantId = "tenantB" });

        // Landed on the tenant's broker (B)...
        (await subOnB.WaitForMessageAsync(15.Seconds())).ShouldBeTrue();
        // ...and NOT on the shared/default broker (A).
        (await subOnA.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
    }

    [Fact]
    public async Task default_message_falls_back_to_the_shared_broker()
    {
        var topic = $"tenant/{Guid.NewGuid():N}";

        await using var subOnA = await RawMqttSubscriber.StartAsync(_portA, topic);
        await using var subOnB = await RawMqttSubscriber.StartAsync(_portB, topic);

        using var host = await buildSenderAsync(topic);

        await host.MessageBus().SendAsync(new TenantColor("no-tenant"));

        // Falls back to the shared/default broker (TenantedIdBehavior.FallbackToDefault)...
        (await subOnA.WaitForMessageAsync(15.Seconds())).ShouldBeTrue();
        // ...and NOT the tenant broker.
        (await subOnB.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
    }

    [Fact]
    public async Task tenant_message_is_consumed_and_stamped_with_the_tenant_id()
    {
        var topic = $"tenant/{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantInbound";
                configureTransport(opts, topic);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToMqttTopic(topic).SendInline();
                opts.ListenToMqttTopic(topic);
            }).StartAsync();

        // The default listener consumes broker A and the tenant listener consumes broker B; the message only
        // exists on broker B, so only the tenant listener consumes it and stamps the tenant id.
        var session = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<TenantColor>(host)
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new TenantColor("for-tenant-b"), new DeliveryOptions { TenantId = "tenantB" }));

        var received = session.Received.SingleEnvelope<TenantColor>();
        received.TenantId.ShouldBe("tenantB");
        received.Message.ShouldBeOfType<TenantColor>().Color.ShouldBe("for-tenant-b");
    }

    [Fact]
    public async Task tenant_client_gets_a_unique_client_id()
    {
        var topic = $"tenant/{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(topic);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<MqttTransport>();

        var tenant = transport.Tenants["tenantB"];
        tenant.Client.ShouldNotBeNull();

        // The tenant ClientId is unique and identifiable — required so the broker doesn't kick a duplicate.
        tenant.Options.ClientOptions.ClientId.ShouldEndWith("-tenant-tenantB");
        tenant.Options.ClientOptions.ClientId.ShouldNotBe(transport.Options.ClientOptions.ClientId);
    }

    [Fact]
    public async Task tenant_aware_endpoint_resolves_a_TenantedSender()
    {
        var topic = $"tenant/{Guid.NewGuid():N}";
        using var host = await buildSenderAsync(topic);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<MqttTransport>();
        var endpoint = transport.Topics[topic];

        // buildSenderAsync publishes inline, so the endpoint resolves an InlineSendingAgent around our sender.
        var agent = (InlineSendingAgent)runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri);
        agent.Sender.ShouldBeOfType<TenantedSender>();
    }

    private void configureTransport(WolverineOptions opts, string topic)
    {
        opts.UseMqttWithLocalBroker(_portA)
            .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
            .AddTenant("tenantB", builder =>
                builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));
    }

    private Task<IHost> buildSenderAsync(string topic)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PerTenantSender";
                configureTransport(opts, topic);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToMqttTopic(topic).SendInline();
            }).StartAsync();
    }
}

public record TenantColor(string Color);

public static class TenantColorHandler
{
    public static void Handle(TenantColor message)
    {
        // no-op; presence lets Wolverine discover a handler so receive tests can track processing
    }
}
