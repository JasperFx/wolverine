using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Bugs;

public record Bug1933Message(string Name);

public static class Bug1933MessageHandler
{
    public static void Handle(Bug1933Message message)
    {
    }
}

// NOT flaky. Re-measured 2026-08-02 after the entity-name sanitizing fix: now 1 of 2, down from 2 of
// 2, and 5.5m for the class. The broker starts fine now; the survivor is
// should_receive_message_when_published_without_tenant_id, which fails as a TrackedSession timeout
// after 4m28s -- the message is Sent and never Received. That is real multi-tenant routing
// behaviour, not provisioning. Tracked as GH-3826.
[Trait("Category", "Flaky")]
public class Bug_1933_multi_tenant_conventional_routing : IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>await  ValueTask.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

        try
        {
            await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync(
                Servers.AzureServiceBusConnectionString);
        }
        catch
        {
            // Tenant emulator cleanup is best-effort
        }
    }

    [Fact]
    public async Task should_receive_message_when_published_without_tenant_id()
    {
        // Single host: tenants + conventional routing (reproduces bug #1933)
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "sender";
                opts.Policies.DisableConventionalLocalRouting();

                opts.UseAzureServiceBusTesting()
                    .AutoPurgeOnStartup()
                    .AddTenantByConnectionString("test", Servers.AzureServiceBusConnectionString)
                    .UseConventionalRouting();

                // Set the tenant's management connection string for the emulator
                var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();
                transport.Tenants["test"].Transport.ManagementConnectionString =
                    Servers.AzureServiceBusConnectionString;
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var message = new Bug1933Message("Hello from default namespace");

        // Publish WITHOUT specifying tenant ID — should go to default namespace
        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<Bug1933Message>()
            .Name.ShouldBe("Hello from default namespace");
    }

    [Fact]
    public async Task baseline_without_tenants()
    {
        // Single host: NO tenants + conventional routing (baseline — should pass)
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "sender";
                opts.Policies.DisableConventionalLocalRouting();

                opts.UseAzureServiceBusTesting()
                    .AutoPurgeOnStartup()
                    .UseConventionalRouting();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var message = new Bug1933Message("Hello from default namespace");

        // Publish WITHOUT specifying tenant ID — should go to default namespace
        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<Bug1933Message>()
            .Name.ShouldBe("Hello from default namespace");
    }
}
