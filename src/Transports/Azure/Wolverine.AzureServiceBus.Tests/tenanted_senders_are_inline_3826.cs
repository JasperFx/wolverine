using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public record Tenanted3826Message(string Name);

public static class Tenanted3826MessageHandler
{
    public static void Handle(Tenanted3826Message message)
    {
    }
}

/// <summary>
/// GH-3826. TenantedSender deliberately does not implement ISenderRequiresCallback (GH-2361), so
/// EndpointCollection never calls RegisterCallback on the senders underneath it. A BatchedSender in
/// that position therefore throws "This sender has not been registered." on every batch, and a
/// tenanted Azure Service Bus endpoint could never send anything at all -- not even on the
/// untenanted default pathway. Every sender under a TenantedSender has to be fire-and-forget.
/// </summary>
public class tenanted_senders_are_inline_3826 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "tenanted3826";
                opts.Policies.DisableConventionalLocalRouting();

                opts.UseAzureServiceBusTesting()
                    .AutoPurgeOnStartup()
                    .AddTenantByConnectionString("tenant1", Servers.AzureServiceBusConnectionString);

                var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();
                transport.Tenants["tenant1"].Transport.ManagementConnectionString =
                    Servers.AzureServiceBusManagementConnectionString;

                opts.PublishMessage<Tenanted3826Message>()
                    .ToAzureServiceBusQueue("tenanted-3826")
                    .BufferedInMemory();

                opts.ListenToAzureServiceBusQueue("tenanted-3826").BufferedInMemory();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void every_sender_under_the_tenanted_sender_is_fire_and_forget()
    {
        var runtime = _host.GetRuntime();
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri("asb://queue/tenanted-3826"))
            .ShouldBeOfType<BufferedSendingAgent>();

        var tenanted = agent.Sender.ShouldBeOfType<TenantedSender>();

        // The default fallback -- the pathway an untenanted publish takes -- and the tenant sender
        // both have to be inline. A BatchedSender here has no callback and fails every send.
        tenanted.DefaultSender.ShouldBeOfType<InlineAzureServiceBusSender>();
        tenanted.TenantSenders().Select(x => x.Value)
            .ShouldAllBe(x => x is InlineAzureServiceBusSender);
    }

    [Fact]
    public async Task can_actually_send_without_a_tenant_id()
    {
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new Tenanted3826Message("no tenant"));

        session.Received.SingleMessage<Tenanted3826Message>()
            .Name.ShouldBe("no tenant");
    }
}
