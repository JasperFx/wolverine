using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class end_to_end : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("send_and_receive");

                opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }
    
    
    [Fact]
    public void builds_response_and_retry_queue_by_default()
    {
        var transport = _host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<AzureServiceBusQueue>().ToArray();
        
        endpoints.ShouldContain(x => x.QueueName.StartsWith("wolverine.response."));
        endpoints.ShouldContain(x => x.QueueName.StartsWith("wolverine.retries."));
    }

    [Fact]
    public async Task disable_system_queues()
    {
        #region sample_disable_system_queues_in_azure_service_bus

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup()
                    .SystemQueuesAreEnabled(false);

                opts.ListenToAzureServiceBusQueue("send_and_receive");

                opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
            }).StartAsync();

        #endregion
        
        var transport = host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        
        var endpoints = transport
            .Endpoints()
            .Where(x => x.Role == EndpointRole.System)
            .OfType<AzureServiceBusQueue>().ToArray();
        
        endpoints.Any().ShouldBeFalse();

    }

    [Fact]
    public async Task send_and_receive_a_single_message()
    {
        var message = new AsbMessage("Josh Allen");

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(5.Minutes())
            .SendMessageAndWaitAsync(message);

        session.Received.SingleMessage<AsbMessage>()
            .Name.ShouldBe(message.Name);
    }
}

public record AsbMessage(string Name);

public static class AsbMessageHandler
{
    public static void Handle(AsbMessage message)
    {
        // nothing
    }
}