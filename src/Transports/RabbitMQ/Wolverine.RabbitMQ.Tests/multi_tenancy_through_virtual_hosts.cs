using System.Diagnostics;
using System.Net;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NSubstitute.ReceivedExtensions;
using Oakton.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

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

public class MultiTenantedRabbitFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await declareVirtualHost("vh1");
        await declareVirtualHost("vh2");
        await declareVirtualHost("vh3");

        Main = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                
                opts.ServiceName = "main";
                
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup()
                    .AddTenant("one", "vh1")
                    .AddTenant("two", "vh2")
                    .AddTenant("three", "vh3");

                // Listen for multiples
                opts.ListenToRabbitQueue("multi_response");

                opts.PublishMessage<MultiTenantMessage>().ToRabbitQueue("multi_incoming");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        One = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "one";
                opts.UseRabbitMq(f => f.VirtualHost = "vh1");
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
                
                
            }).StartAsync();
        
        Two = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "two";
                opts.UseRabbitMq(f => f.VirtualHost = "vh2");
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        Three = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "three";
                opts.UseRabbitMq(f => f.VirtualHost = "vh3");
                opts.ListenToRabbitQueue("multi_incoming");
                
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
    
    private static async Task<HttpResponseMessage> declareVirtualHost(string vhname)
    {
        var credentials = new NetworkCredential("guest", "guest");
        using var handler = new HttpClientHandler { Credentials = credentials };
        using var client = new HttpClient(handler);
        

        var request = new HttpRequestMessage(HttpMethod.Put, $"http://localhost:15672/api/vhosts/{vhname}");
        

        var response = await client.SendAsync(request);
        return response;
    }
}

public class multi_tenancy_through_virtual_hosts : IClassFixture<MultiTenantedRabbitFixture>
{
    private readonly MultiTenantedRabbitFixture _fixture;

    public multi_tenancy_through_virtual_hosts(MultiTenantedRabbitFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
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
}