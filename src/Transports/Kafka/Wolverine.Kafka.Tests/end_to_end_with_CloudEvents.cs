using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class end_to_end_with_CloudEvents : IAsyncLifetime
{
    private IHost _receiver;
    private IHost _sender;

    public async Task InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                //opts.EnableAutomaticFailureAcks = false;
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.ListenToKafkaTopic("cloudevents")

                    // You do have to tell Wolverine what the message type
                    // is that you'll receive here so that it can deserialize the 
                    // incoming data
                    .InteropWithCloudEvents();

                opts.Services.AddResourceSetupOnStartup();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka");

                opts.Services.AddResourceSetupOnStartup();

                opts.Policies.UseDurableInboxOnAllListeners();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().ToKafkaTopic("cloudevents")
                    .InteropWithCloudEvents();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }

    [Fact]
    public async Task end_to_end()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"));

        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("yellow");
    }
}