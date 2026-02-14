using Azure.Messaging.ServiceBus.Administration;
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

public class Bug_1933_multi_tenant_conventional_routing : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

        try
        {
            await DeleteTenantEmulatorObjectsAsync();
        }
        catch
        {
            // Tenant emulator cleanup is best-effort
        }
    }

    private static async Task DeleteTenantEmulatorObjectsAsync()
    {
        var client = new ServiceBusAdministrationClient(Servers.AzureServiceBusTenantManagementConnectionString);

        await foreach (var topic in client.GetTopicsAsync())
        {
            await client.DeleteTopicAsync(topic.Name);
        }

        await foreach (var queue in client.GetQueuesAsync())
        {
            await client.DeleteQueueAsync(queue.Name);
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
                    .AddTenantByConnectionString("test", Servers.AzureServiceBusTenantConnectionString)
                    .UseConventionalRouting();

                // Set the tenant's management connection string for the emulator
                var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();
                transport.Tenants["test"].Transport.ManagementConnectionString =
                    Servers.AzureServiceBusTenantManagementConnectionString;
            }).StartAsync();

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
            }).StartAsync();

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
