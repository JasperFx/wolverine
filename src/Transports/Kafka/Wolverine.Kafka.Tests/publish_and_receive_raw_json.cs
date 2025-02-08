using System.Text.Json;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class publish_and_receive_raw_json : IAsyncLifetime
{
    private IHost _sender;
    private IHost _receiver;

    public async Task InitializeAsync()
    {

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.ListenToKafkaTopic("json")
                    .ReceiveRawJson<ColorMessage>();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().ToKafkaTopic("json").PublishRawJson(new JsonSerializerOptions());
            }).StartAsync();
    }

    [Fact]
    public async Task can_receive_pure_json_if_the_default_messsage_type_exists()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"));

        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("yellow");
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}