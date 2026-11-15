using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.MQTT.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.MQTT.Tests;

/// <summary>
/// GH-5196 against two real (in-process) MQTT brokers: A is the shared connection, B the tenant's own. Under the 7.0
/// default, a send to an unknown tenant reaches neither broker, a send to a registered tenant reaches only that
/// tenant's broker, and a message received with an unknown tenant id is dead lettered
/// </summary>
[Collection("acceptance")]
public class strict_tenancy_5196 : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private LocalMqttBroker _brokerA = null!;
    private LocalMqttBroker _brokerB = null!;
    private int _portA;
    private int _portB;

    public strict_tenancy_5196(ITestOutputHelper output) => _output = output;

    public async ValueTask InitializeAsync()
    {
        _portA = PortFinder.GetAvailablePort();
        _portB = PortFinder.GetAvailablePort();

        _brokerA = new LocalMqttBroker(_portA) { Logger = new XUnitLogger(_output, "MQTT-A") };
        _brokerB = new LocalMqttBroker(_portB) { Logger = new XUnitLogger(_output, "MQTT-B") };

        await _brokerA.StartAsync();
        await _brokerB.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _brokerA.DisposeAsync();
        await _brokerB.DisposeAsync();
    }

    private Task<IHost> buildSenderAsync(string topic)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancySender";

                // No TenantIdBehavior(): the 7.0 default
                opts.UseMqttWithLocalBroker(_portA)
                    .AddTenant("tenantB", builder =>
                        builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToMqttTopic(topic).SendInline();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_7_0_default_dead_letters_unknown_tenants()
    {
        using var host = await buildSenderAsync($"strict/{Guid.NewGuid():N}");
        host.GetRuntime().Options.Transports.GetOrCreate<MqttTransport>().TenantedIdBehavior
            .ShouldBe(TenantedIdBehavior.DeadLetterUnknownTenants);
    }

    [Fact]
    public async Task an_unknown_tenant_throws_without_a_message_store_and_reaches_neither_broker()
    {
        var topic = $"strict/{Guid.NewGuid():N}";

        await using var subOnA = await RawMqttSubscriber.StartAsync(_portA, topic);
        await using var subOnB = await RawMqttSubscriber.StartAsync(_portB, topic);

        using var host = await buildSenderAsync(topic);

        var ex = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await host.MessageBus().SendAsync(new TenantColor("unknown"), new DeliveryOptions { TenantId = "tenantZ" }));
        ex.TenantId.ShouldBe("tenantZ");

        (await subOnA.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
        (await subOnB.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
    }

    [Fact]
    public async Task a_known_tenant_reaches_only_its_own_broker()
    {
        var topic = $"strict/{Guid.NewGuid():N}";

        await using var subOnA = await RawMqttSubscriber.StartAsync(_portA, topic);
        await using var subOnB = await RawMqttSubscriber.StartAsync(_portB, topic);

        using var host = await buildSenderAsync(topic);

        await host.MessageBus().SendAsync(new TenantColor("known"), new DeliveryOptions { TenantId = "tenantB" });

        (await subOnB.WaitForMessageAsync(15.Seconds())).ShouldBeTrue();
        (await subOnA.WaitForMessageAsync(2.Seconds())).ShouldBeFalse();
    }

    [Fact]
    public async Task a_received_message_for_an_unknown_tenant_is_dead_lettered()
    {
        var topic = $"strict/{Guid.NewGuid():N}";

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancyReceiver";
                opts.UseMqttWithLocalBroker(_portA)
                    .AddTenant("tenantB", builder =>
                        builder.WithClientOptions(o => o.WithTcpServer("127.0.0.1", _portB)));

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToMqttTopic(topic);
            }).StartAsync(TestContext.Current.CancellationToken);

        // A node with no tenants puts the tenant id on the shared broker as a property
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "UntenantedSender";
                opts.UseMqttWithLocalBroker(_portA);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<TenantColor>().ToMqttTopic(topic).SendInline();
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(new TenantColor("unknown"), new DeliveryOptions { TenantId = "tenantZ" });

        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue).ShouldHaveSingleItem();
        record.Exception.ShouldBeOfType<UnknownBrokerTenantException>().TenantId.ShouldBe("tenantZ");
        session.Executed.MessagesOf<TenantColor>().ShouldBeEmpty();
    }
}
