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
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().ToKafkaTopic("cloudevents")
                    .UseInterop((runtime, topic) => new CloudEventsOnlyMapper(new CloudEventsMapper(runtime.Options.HandlerGraph, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
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