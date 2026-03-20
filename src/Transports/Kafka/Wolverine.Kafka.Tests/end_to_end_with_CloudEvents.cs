using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using IntegrationTests;
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

[Trait("Category", "Flaky")]
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

    public async Task DisposeAsync()
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
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"));

        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("yellow");
    }
}

public class inline_end_to_end_with_CloudEvents : IAsyncLifetime
{
    private IHost _receiver;
    private IHost _sender;

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
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

    public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing)
    {
        outgoing.Key = envelope.GroupId;
        outgoing.Value = _cloudEvents.WriteToBytes(envelope);
    }

    public void MapIncomingToEnvelope(Envelope envelope, Message<string, byte[]> incoming)
    {
        throw new NotImplementedException();
    }
}
