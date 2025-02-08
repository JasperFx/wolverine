using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class broadcast_to_topic_async : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _sender;
    private IHost _receiver;

    public broadcast_to_topic_async(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.ListenToKafkaTopic("incoming.one");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

    }

    [Fact]
    public async Task broadcast()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.BroadcastToTopicAsync("incoming.one", new ColorMessage("blue")));

        var received = session.Received.SingleMessage<ColorMessage>();
        received.Color.ShouldBe("blue");
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}

public class ColorMessage
{
    public ColorMessage()
    {
    }

    public ColorMessage(string color)
    {
        Color = color;
    }

    public string Color { get; set; }
}

public static class ColorMessageHandler
{
    public static void Handle(ColorMessage message)
    {
        Debug.WriteLine("Got " + message.Color);
    }
}