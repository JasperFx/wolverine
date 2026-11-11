using System.Text;
using System.Text.Json;
using Dekaf.Consumer;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

internal static class CloudEventsKafkaTestConstants
{
    public const string ColorMessageTypeAlias = "wolverine.kafka.tests.color";
}

public class end_to_end_with_CloudEvents : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                //opts.EnableAutomaticFailureAcks = false;
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.RegisterMessageType(typeof(ColorMessage), CloudEventsKafkaTestConstants.ColorMessageTypeAlias);
                opts.ListenToKafkaTopic("cloudevents").InteropWithCloudEvents();

                // Include test assembly for handler discovery
                opts.Discovery.IncludeAssembly(GetType().Assembly);

                opts.Services.AddResourceSetupOnStartup();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka");

                opts.Services.AddResourceSetupOnStartup();

                opts.Policies.UseDurableInboxOnAllListeners();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.RegisterMessageType(typeof(ColorMessage), CloudEventsKafkaTestConstants.ColorMessageTypeAlias);

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().ToKafkaTopic("cloudevents")
                    .UseInterop((runtime, topic) => new CloudEventsOnlyMapper(new CloudEventsMapper(runtime.Options.HandlerGraph, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task end_to_end()
    {
        var session = await _sender.TrackActivity()
            .Timeout(60.Seconds())
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"));

        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("yellow");
    }

    [Fact]
    public async Task custom_headers_survive_a_mapper_that_only_writes_the_cloud_event()
    {
        // GH-5080: the sender's mapper writes no Kafka headers, so they have to be in the document
        var options = new DeliveryOptions();
        options.Headers["x-user"] = "alice";
        options.Headers["x-country"] = "ca";

        var session = await _sender.TrackActivity()
            .Timeout(60.Seconds())
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"), options);

        var envelope = session.Received.SingleEnvelope<ColorMessage>();
        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
    }
}

// GH-5080: InteropWithCloudEvents() on both sides of the topic
public class interop_with_CloudEvents_on_both_sides : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.RegisterMessageType(typeof(ColorMessage), CloudEventsKafkaTestConstants.ColorMessageTypeAlias);
                opts.ListenToKafkaTopic("cloudevents-both").InteropWithCloudEvents();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.RegisterMessageType(typeof(ColorMessage), CloudEventsKafkaTestConstants.ColorMessageTypeAlias);
                opts.PublishAllMessages().ToKafkaTopic("cloudevents-both").InteropWithCloudEvents();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task custom_headers_survive()
    {
        var options = new DeliveryOptions();
        options.Headers["x-user"] = "alice";
        options.Headers["x-country"] = "ca";

        var session = await _sender.TrackActivity()
            .Timeout(60.Seconds())
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"), options);

        var envelope = session.Received.SingleEnvelope<ColorMessage>();
        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
    }
}

public class inline_end_to_end_with_CloudEvents : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.RegisterMessageType(typeof(ColorMessage), CloudEventsKafkaTestConstants.ColorMessageTypeAlias);
                opts.ListenToKafkaTopic("cloudevents-inline")
                    .InteropWithCloudEvents()
                    .ProcessInline();

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();
                opts.RegisterMessageType(typeof(ColorMessage), CloudEventsKafkaTestConstants.ColorMessageTypeAlias);

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().ToKafkaTopic("cloudevents-inline")
                    .UseInterop((runtime, topic) => new CloudEventsOnlyMapper(new CloudEventsMapper(runtime.Options.HandlerGraph, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task end_to_end_without_default_incoming_message_type()
    {
        var session = await _sender.TrackActivity()
            .Timeout(60.Seconds())
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"));

        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("yellow");
    }
}

internal class CloudEventsOnlyMapper : IKafkaEnvelopeMapper
{
    private readonly CloudEventsMapper _cloudEvents;

    public CloudEventsOnlyMapper(CloudEventsMapper cloudEvents)
    {
        _cloudEvents = cloudEvents;
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, OutgoingKafkaRecord outgoing)
    {
        outgoing.Key = envelope.GroupId!;
        outgoing.Value = _cloudEvents.WriteToBytes(envelope);
    }

    public void MapIncomingToEnvelope(Envelope envelope, ConsumeResult<string, byte[]> incoming)
    {
        throw new NotImplementedException();
    }
}
