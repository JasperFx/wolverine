using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.AzureServiceBus.Tests;

public class end_to_end_with_named_broker
{
    public static async Task bootstrap_with_named_brokers()
    {
        #region sample_using_named_azure_service_bus_broker

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var connectionString1 = builder.Configuration.GetConnectionString("azureservicebus1");
            opts.AddNamedAzureServiceBusBroker(new BrokerName("one"), connectionString1);
            
            var connectionString2 = builder.Configuration.GetConnectionString("azureservicebus2");
            opts.AddNamedAzureServiceBusBroker(new BrokerName("two"), connectionString2);

            opts.PublishAllMessages().ToAzureServiceBusQueueOnNamedBroker(new BrokerName("one"), "queue1");

            opts.ListenToAzureServiceBusQueueOnNamedBroker(new BrokerName("two"), "incoming");

            opts.ListenToAzureServiceBusSubscriptionOnNamedBroker(new BrokerName("two"), "subscription1");
        });

        #endregion
    }
    
    private readonly ITestOutputHelper _output;
    private readonly BrokerName theName = new BrokerName("other");

    public end_to_end_with_named_broker(ITestOutputHelper output)
    {
        _output = output;
    }
    
    //[Fact]
    public async Task send_message_to_and_receive_through_rabbitmq_with_inline_receivers()
    {
        var queueName = Guid.NewGuid().ToString();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.AddNamedAzureServiceBusBroker(theName, "REPLACE ME").SystemQueuesAreEnabled(false).AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToAzureServiceBusQueueOnNamedBroker(theName, queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.AddNamedAzureServiceBusBroker(theName, "REPLACE ME").SystemQueuesAreEnabled(false).AutoProvision();

            opts.ListenToAzureServiceBusQueueOnNamedBroker(theName, queueName).ProcessInline().Named(queueName);
            opts.Services.AddSingleton<ColorHistory>();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState();

        for (int i = 0; i < 10; i++)
        {
            await publisher.SendAsync(new ColorChosen { Name = "blue" });
        }

        var cancellation = new CancellationTokenSource(30.Seconds());
        var queue = receiver.Get<IWolverineRuntime>().Endpoints.EndpointByName(queueName).ShouldBeOfType<AzureServiceBusQueue>();

        while (!cancellation.IsCancellationRequested && await queue.QueuedCountAsync() > 0)
        {
            await Task.Delay(250.Milliseconds(), cancellation.Token);
        }

        cancellation.Token.ThrowIfCancellationRequested();


    }

    [Fact]
    public async Task correct_scheme_on_reply_uri()
    {
        var queueName = Guid.NewGuid().ToString();
        using var publisher = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Publisher";
            opts.Discovery.DisableConventionalDiscovery();
            
            opts.AddNamedAzureServiceBusBroker(theName, "REPLACE ME")
                .AutoProvision().AutoPurgeOnStartup();

            opts.PublishAllMessages()
                .ToAzureServiceBusQueueOnNamedBroker(theName, queueName)
                .SendInline();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });


        using var receiver = WolverineHost.For(opts =>
        {
            opts.ServiceName = "Receiver";
            opts.UseAzureServiceBusTesting().AutoProvision();

            opts.ListenToAzureServiceBusQueue(queueName).Named(queueName);
            
            opts.Services.AddSingleton<ColorHistory>();

            opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
        });

        await receiver.ResetResourceState();

        var request = new RequestId(Guid.NewGuid());
        var (tracked, response) =
            await publisher.TrackActivity().AlsoTrack(receiver).InvokeAndWaitAsync<ResponseId>(request);
        
        response.Id.ShouldBe(request.Id);
        tracked.Received.SingleEnvelope<ResponseId>().Destination.Scheme.ShouldBe("other");
    }

}

public record RequestId(Guid Id);
public record ResponseId(Guid Id);

public static class RequestIdHandler
{
    public static ResponseId Handle(RequestId message) => new ResponseId(message.Id);
}