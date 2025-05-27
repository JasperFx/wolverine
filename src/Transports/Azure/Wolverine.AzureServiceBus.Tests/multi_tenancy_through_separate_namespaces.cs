using System.Diagnostics;
using System.Net;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Oakton.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public record MultiTenantMessage(Guid Id);
public record MultiTenantResponse(Guid Id);

public static class MultiTenantMessageHandler
{
    public static object Handle(MultiTenantMessage message)
    {
        return new MultiTenantResponse(message.Id).ToDestination("rabbitmq://queue/multi_response".ToUri());
    }

    public static void Handle(MultiTenantResponse message) => Debug.WriteLine("Got a response");
}

public class MultiTenantedAzureServiceBusFixture : IAsyncLifetime
{
    public const string Tenant1ConnectionString = "REPLACE ME";
    public const string Tenant2ConnectionString = "REPLACE ME";
    public const string Tenant3ConnectionString = "REPLACE ME";
    
    public async Task InitializeAsync()
    {
        Main = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                
                opts.ServiceName = "main";
                
                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup()
                    .AddTenantByConnectionString("one", Tenant1ConnectionString)
                    .AddTenantByConnectionString("two", Tenant1ConnectionString)
                    .AddTenantByConnectionString("three", Tenant1ConnectionString);

                // Listen for multiples
                opts.ListenToAzureServiceBusQueue("multi_response");

                opts.ListenToAzureServiceBusQueue("global_response").GlobalListener();

                // Really just using this to test the construction of senders and listeners
                opts.PublishMessage<Message1>().ToAzureServiceBusQueue("message1");
                opts.PublishMessage<Message2>().ToAzureServiceBusQueue("message2").GlobalSender();

                opts.PublishMessage<MultiTenantMessage>().ToAzureServiceBusQueue("multi_incoming");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        One = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "one";
                opts.UseAzureServiceBus(Tenant1ConnectionString);
                opts.ListenToAzureServiceBusQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
                
                
            }).StartAsync();
        
        Two = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "two";
                opts.UseAzureServiceBus(Tenant2ConnectionString);
                opts.ListenToAzureServiceBusQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        Three = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "three";
                opts.UseAzureServiceBus(Tenant3ConnectionString);
                opts.ListenToAzureServiceBusQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public IHost Three { get; set; }

    public IHost Two { get; set; }

    public IHost One { get; set; }

    public IHost Main { get; private set; }

    public async Task DisposeAsync()
    {
        await Main.StopAsync();
        await One.StopAsync();
        await Two.StopAsync();
        await Three.StopAsync();
    }

}

public class multi_tenancy_through_separate_namespaces : IClassFixture<MultiTenantedAzureServiceBusFixture>
{
    private readonly MultiTenantedAzureServiceBusFixture _fixture;

    public multi_tenancy_through_separate_namespaces(MultiTenantedAzureServiceBusFixture fixture)
    {
        _fixture = fixture;
    }

    //[Fact]
    public async Task send_message_to_a_specific_tenant()
    {
        var message = new MultiTenantMessage(Guid.NewGuid());
        var session = await _fixture.Main
            .TrackActivity()
            .AlsoTrack(_fixture.One, _fixture.Two, _fixture.Three)
            .WaitForMessageToBeReceivedAt<MultiTenantMessage>(_fixture.Two)
            .SendMessageAndWaitAsync(message, new DeliveryOptions{TenantId = "two"});

        var record = session.Received.SingleRecord<MultiTenantMessage>();
        record.ServiceName.ShouldBe("two");

        var response = session.Received.SingleRecord<MultiTenantResponse>();
        response.ServiceName.ShouldBe("main");
        
        // Label the envelope as tenant id = "two" because it was received at that point
        response.Envelope.TenantId.ShouldBe("two");
        response.Envelope.Message.ShouldBeOfType<MultiTenantResponse>()
            .Id.ShouldBe(message.Id);
    }
    
    /*

       opts.PublishMessage<Message3>().ToRabbitExchange("message3");
       opts.PublishMessage<Message4>().ToRabbitExchange("message4").GlobalSender();
     */

}