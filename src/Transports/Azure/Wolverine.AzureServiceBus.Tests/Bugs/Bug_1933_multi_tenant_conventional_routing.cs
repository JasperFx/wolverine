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
    public async ValueTask InitializeAsync() =>await  ValueTask.CompletedTask;

    // The "test" tenant is the same emulator as the default namespace, so one wipe cleans up both
    public async ValueTask DisposeAsync() => await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    /// <summary>
    /// Conventional routing only for this repro's own message. Left unrestricted, the convention provisions and purges
    /// a queue for every handled message type in this assembly, in both namespaces. That was most of this class's 207
    /// seconds, and it has nothing to do with GH-1933
    /// </summary>
    private static void onlyTheBug1933Message(AzureServiceBusMessageRoutingConvention convention)
        => convention.IncludeTypes(t => t == typeof(Bug1933Message));

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
                    .UseConventionalRouting(onlyTheBug1933Message);

                // Set the tenant's management connection string for the emulator. This has to be the
                // *management* endpoint -- pointing it at the AMQP endpoint made every tenant
                // provisioning call hang until its own timeout, which is where this test's old
                // 4m+ runtime came from.
                var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();
                transport.Tenants["test"].Transport.ManagementConnectionString =
                    Servers.AzureServiceBusManagementConnectionString;
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
                    .UseConventionalRouting(onlyTheBug1933Message);
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
