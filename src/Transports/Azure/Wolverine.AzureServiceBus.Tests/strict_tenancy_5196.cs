using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public record StrictTenancyColor(string Color);

public class StrictTenancyColorHandler
{
    public static void Handle(StrictTenancyColor message)
    {
    }
}

/// <summary>
/// GH-5196 against two Azure Service Bus emulators (asb-emulator and asb-emulator-2 in docker-compose): the first is
/// the shared namespace, the second the tenant's own. Under the 7.0 default, a send to an unknown tenant reaches
/// neither namespace, a send to a registered tenant reaches only its namespace, and a message received with an
/// unknown tenant id is dead lettered
/// </summary>
[Collection("acceptance")]
public class strict_tenancy_5196 : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        // Nothing else in the suite uses the tenant emulator, so nothing else cleans it up
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync(Servers.AzureServiceBusTenantManagementConnectionString);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void configureTenantedTransport(WolverineOptions opts)
    {
        // No TenantIdBehavior(): the 7.0 default
        // UseAzureServiceBusTesting(), not UseAzureServiceBusEmulator(): the suite deletes every object in the shared
        // emulator the first time it is called, and that must not happen after this host has provisioned its queues
        opts.UseAzureServiceBusTesting()
            .AutoPurgeOnStartup()
            .AddTenantByConnectionString("tenantB", Servers.AzureServiceBusTenantConnectionString);

        // The tenant's emulator takes administration on its own management port
        opts.AzureServiceBusTransport().Tenants["tenantB"].Transport.ManagementConnectionString =
            Servers.AzureServiceBusTenantManagementConnectionString;
    }

    private static Task<IHost> buildTenantedHostAsync(string queue)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "StrictTenancy";
                configureTenantedTransport(opts);

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery().IncludeType<StrictTenancyColorHandler>();
                opts.PublishMessage<StrictTenancyColor>().ToAzureServiceBusQueue(queue).SendInline();
                opts.ListenToAzureServiceBusQueue(queue);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_unknown_tenant_reaches_neither_namespace_while_known_and_untenanted_messages_go_where_they_belong()
    {
        const string queue = "strict-tenancy-5196";
        using var host = await buildTenantedHostAsync(queue);

        UnknownBrokerTenantException? unknown = null;

        // The listener consumes both namespaces. The unknown tenant goes first; the two later messages are the proof
        // that nothing for it ever arrived in either namespace
        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(120.Seconds())
            .WaitForCondition(new ReceivedCount(2))
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
            {
                unknown = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
                    await c.SendAsync(new StrictTenancyColor("unknown"), new DeliveryOptions { TenantId = "tenantZ" }));

                await c.SendAsync(new StrictTenancyColor("known"), new DeliveryOptions { TenantId = "tenantB" });
                await c.SendAsync(new StrictTenancyColor("untenanted"));
            }));

        unknown.ShouldNotBeNull().TenantId.ShouldBe("tenantZ");

        // Give a wrongly sent message a moment longer to show up behind the other two
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        var received = session.Received.Envelopes().Where(x => x.Message is StrictTenancyColor).ToArray();
        received.Select(x => ((StrictTenancyColor)x.Message!).Color).OrderBy(x => x)
            .ShouldBe(["known", "untenanted"]);
        received.Single(x => ((StrictTenancyColor)x.Message!).Color == "known").TenantId.ShouldBe("tenantB");
    }

    [Fact]
    public async Task a_received_message_for_an_unknown_tenant_is_dead_lettered()
    {
        const string queue = "strict-tenancy-5196-receive";
        using var receiver = await buildTenantedHostAsync(queue);

        // A node with no tenants puts the tenant id on the shared namespace as an application property
        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "UntenantedSender";
                opts.UseAzureServiceBusTesting();

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<StrictTenancyColor>().ToAzureServiceBusQueue(queue).SendInline();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var session = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(60.Seconds())
            .SendMessageAndWaitAsync(new StrictTenancyColor("unknown"), new DeliveryOptions { TenantId = "tenantZ" });

        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue).ShouldHaveSingleItem();
        record.Exception.ShouldBeOfType<UnknownBrokerTenantException>().TenantId.ShouldBe("tenantZ");
        session.Executed.MessagesOf<StrictTenancyColor>().ShouldBeEmpty();
    }

    private class ReceivedCount(int expected) : ITrackedCondition
    {
        private int _count;

        public void Record(EnvelopeRecord record)
        {
            if (record.MessageEventType == MessageEventType.Received && record.Message is StrictTenancyColor)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public bool IsCompleted() => Volatile.Read(ref _count) >= expected;
    }
}
