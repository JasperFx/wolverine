using System.Diagnostics;
using System.Net;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
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
    public async ValueTask InitializeAsync()
    {
        await declareVirtualHost("vh1");
        await declareVirtualHost("vh2");
        await declareVirtualHost("vh3");

        Main = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                
                opts.ServiceName = "main";
                
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing()
                    .AddTenant("one", "vh1")
                    .AddTenant("two", "vh2")
                    .AddTenant("three", "vh3");
                
                // Really just to manually test https://github.com/JasperFx/wolverine/issues/1658
                opts.ListenToRabbitQueue("Queue1", conf =>
                {
                    conf.BindExchange("Exchange1");
                    conf.BindExchange("Exchange2");
                });

                // Listen for multiples
                opts.ListenToRabbitQueue("multi_response");

                opts.ListenToRabbitQueue("global_response").GlobalListener();

                // Really just using this to test the construction of senders and listeners
                opts.PublishMessage<Message1>().ToRabbitQueue("message1");
                opts.PublishMessage<Message2>().ToRabbitQueue("message2").GlobalSender();
                opts.PublishMessage<Message3>().ToRabbitExchange("message3");
                opts.PublishMessage<Message4>().ToRabbitExchange("message4").GlobalSender();

                opts.PublishMessage<MultiTenantMessage>().ToRabbitQueue("multi_incoming");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        One = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "one";
                opts.UseRabbitMq(f => f.VirtualHost = "vh1").AutoPurgeOnStartup().DisableDeadLetterQueueing();
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
                
                
            }).StartAsync();
        
        Two = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "two";
                opts.UseRabbitMq(f => f.VirtualHost = "vh2").AutoPurgeOnStartup().DisableDeadLetterQueueing();
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
        
        Three = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
                opts.ServiceName = "three";
                opts.UseRabbitMq(f => f.VirtualHost = "vh3").AutoPurgeOnStartup().DisableDeadLetterQueueing();
                opts.ListenToRabbitQueue("multi_incoming");
                
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public IHost Three { get; set; } = null!;

    public IHost Two { get; set; } = null!;

    public IHost One { get; set; } = null!;

    public IHost Main { get; private set; } = null!;

    public async ValueTask DisposeAsync()
    {
        await Main.StopAsync();
        Main.Dispose();
        await One.StopAsync();
        One.Dispose();
        await Two.StopAsync();
        Two.Dispose();
        await Three.StopAsync();
        Three.Dispose();
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

// GH-3763: untagged 2026-08-03, but NOT yet clean -- read this before assuming it is.
//
// The previous note said untagging "would put a guaranteed red in CIRabbitMQ". That is no longer true:
// measured twice, CIRabbitMQ is GREEN at 472 passed with send_message_to_a_specific_tenant failing its
// first attempt and passing on the supervisor's retry. So it is now visible debt in the retry ledger
// (GH-3787) rather than 7 tests running nowhere, which is the trade this file is making on purpose.
//
// Two collisions were found and fixed in this pass, and neither was sufficient:
//   - RabbitTesting handed out queue and exchange names from a static counter that restarts at zero in
//     every worker PROCESS, so classes in different processes declared and bound the same names. See
//     the note on RabbitTesting in end_to_end.cs.
//   - Main purged on startup but the three tenant hosts did not, so a MultiTenantMessage left in
//     multi_incoming on vh1/vh2/vh3 by an earlier run could be received alongside the new one and make
//     SingleRecord throw.
//
// What remains: the class still passes 7/7 alone and still costs exactly one retry in-suite, every run.
// The remaining interference is unidentified.
//
// The next step this note used to ask for -- "dump the tracked session on the FIRST attempt rather than
// infer from the assertion" -- has since been built and no longer needs doing by hand. GH-3787's retry
// ledger records the first failing attempt's error and stack for every retried test, so the dump
// arrives on its own. From main run 30856898284:
//
//     System.Exception : No messages of type Wolverine.RabbitMQ.Tests.MultiTenantResponse were received
//     Activity detected: | Service (Node Id) | Message Id | Message Type | ...
//
// So the request never produced its response, rather than the response going somewhere unexpected.
// That is consistent with the standing suspicion recorded above -- the fixture's Queue1,
// multi_response, global_response and multi_incoming are fixed names in the shared default vhost -- but
// it does not yet prove it. The full tracked-session table and the stack are in the per-run
// test-ledger-CIRabbitMQ artifact; read one before theorising further.
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
            .Timeout(15.Seconds())
            .AlsoTrack(_fixture.One, _fixture.Two, _fixture.Three)
            .WaitForMessageToBeReceivedAt<MultiTenantMessage>(_fixture.Two)
            // The assertions below also require the RESPONSE to have made it back to main. Waiting
            // only on the request meant the session could complete in the same millisecond the
            // response was sent, leaving SingleRecord<MultiTenantResponse>() with nothing to find --
            // a chronic full-suite flake that passed in isolation.
            .WaitForMessageToBeReceivedAt<MultiTenantResponse>(_fixture.Main)
            .SendMessageAndWaitAsync(message, new DeliveryOptions{TenantId = "two"});

        var record = session.Received.SingleRecord<MultiTenantMessage>();
        record.ServiceName.ShouldBe("two");

        var response = session.Received.SingleRecord<MultiTenantResponse>();
        response.ServiceName.ShouldBe("main");
        
        // Label the envelope as tenant id = "two" because it was received at that point
        response.Envelope!.TenantId.ShouldBe("two");
        response.Envelope!.Message.ShouldBeOfType<MultiTenantResponse>()
            .Id.ShouldBe(message.Id);
    }
    
    /*

       opts.PublishMessage<Message3>().ToRabbitExchange("message3");
       opts.PublishMessage<Message4>().ToRabbitExchange("message4").GlobalSender();
     */

    [Fact]
    public void build_compound_sender_for_tenant_aware_exchange()
    {
        var runtime = _fixture.Main.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var exchange = transport.Exchanges["message3"];
        exchange.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);

        var sender = exchange.ResolveSender(runtime);
        sender.ShouldBeOfType<TenantedSender>();
    }
    
    [Fact]
    public void build_simple_sender_for_global_exchange()
    {
        var runtime = _fixture.Main.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var exchange = transport.Exchanges["message4"];
        exchange.TenancyBehavior.ShouldBe(TenancyBehavior.Global);

        var sender = exchange.ResolveSender(runtime);
        sender.ShouldBeOfType<RabbitMqSender>();
    }

    [Fact]
    public void build_compound_sender_for_tenant_aware_queue()
    {
        var runtime = _fixture.Main.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var queue = transport.Queues["message1"];
        queue.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);

        var sender = queue.ResolveSender(runtime);
        sender.ShouldBeOfType<TenantedSender>();
    }
    
    [Fact]
    public void build_simple_sender_for_global_queue()
    {
        var runtime = _fixture.Main.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var queue = transport.Queues["message2"];
        queue.TenancyBehavior.ShouldBe(TenancyBehavior.Global);

        var sender = queue.ResolveSender(runtime);
        sender.ShouldBeOfType<RabbitMqSender>();
    }

    [Fact]
    public async Task opt_into_global_listener_for_queue()
    {
        var runtime = _fixture.Main.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var queue = transport.Queues["global_response"];
        queue.TenancyBehavior.ShouldBe(TenancyBehavior.Global);

        var receiver = Substitute.For<IReceiver>();
        var listener = await queue.BuildListenerAsync(runtime, receiver);
        
        // Not parallel
        listener.ShouldBeOfType<RabbitMqListener>();
    }

    [Fact]
    public async Task use_tenanted_for_listener_when_appropriate()
    {
        var runtime = _fixture.Main.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RabbitMqTransport>();
        var queue = transport.Queues["multi_response"];
        queue.TenancyBehavior.ShouldBe(TenancyBehavior.TenantAware);

        var receiver = Substitute.For<IReceiver>();
        var listener = await queue.BuildListenerAsync(runtime, receiver);
        
        // Not parallel
        listener.ShouldBeOfType<CompoundListener>();
    }
}

public static class MultiTenantedRabbitMqSamples
{
    public static async Task Configure()
    {
        #region sample_configuring_rabbit_mq_for_tenancy
        var builder = Host.CreateApplicationBuilder();

        builder.UseWolverine(opts =>
        {
            // At this point, you still have to have a *default* broker connection to be used for 
            // messaging. 
            opts.UseRabbitMq(new Uri(builder.Configuration.GetConnectionString("main")!))
                
                // This will be respected across *all* the tenant specific
                // virtual hosts and separate broker connections
                .AutoProvision()

                // This is the default, if there is no tenant id on an outgoing message,
                // use the default broker
                .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

                // Or tell Wolverine instead to just quietly ignore messages sent
                // to unrecognized tenant ids
                .TenantIdBehavior(TenantedIdBehavior.IgnoreUnknownTenants)

                // Or be draconian and make Wolverine assert and throw an exception
                // if an outgoing message does not have a tenant id
                .TenantIdBehavior(TenantedIdBehavior.TenantIdRequired)

                // Add specific tenants for separate virtual host names
                // on the same broker as the default connection
                .AddTenant("one", "vh1")
                .AddTenant("two", "vh2")
                .AddTenant("three", "vh3")

                // Or, you can add a broker connection to something completel
                // different for a tenant
                .AddTenant("four", new Uri(builder.Configuration.GetConnectionString("rabbit_four")!));

            // This Wolverine application would be listening to a queue
            // named "incoming" on all virtual hosts and/or tenant specific message
            // brokers
            opts.ListenToRabbitQueue("incoming");

            opts.ListenToRabbitQueue("incoming_global")
                
                // This opts this queue out from being per-tenant, such that
                // there will only be the single "incoming_global" queue for the default
                // broker connection
                .GlobalListener();

            // More on this in the docs....
            opts.PublishMessage<Message1>()
                .ToRabbitQueue("outgoing").GlobalSender();
        });

        #endregion
        
        
    }

    #region sample_send_message_to_specific_tenant
    public static async Task send_message_to_specific_tenant(IMessageBus bus)
    {
        // Send a message tagged to a specific tenant id
        await bus.PublishAsync(new Message1(), new DeliveryOptions { TenantId = "two" });
    }

    #endregion
}