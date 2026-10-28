using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
///     GH-5244. Wolverine on both ends here, speaking the NServiceBus wire format: one sender writes NServiceBus
///     XML, one writes NServiceBus JSON, and a single NServiceBus interop listener reads both. The run against
///     real NServiceBus endpoints is in the wolverine-interop suite, which keeps NServiceBus out of this
///     repository.
/// </summary>
public class nservicebus_xml_interop_5244 : IAsyncLifetime
{
    private readonly string theQueueName = "nsb-xml-" + Guid.NewGuid().ToString("N")[..8];
    private IHost theListener = null!;
    private IHost theXmlSender = null!;
    private IHost theJsonSender = null!;

    public async ValueTask InitializeAsync()
    {
        theListener = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "listener";
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                // Reads NServiceBus messages as JSON or XML, by their NServiceBus.ContentType header
                opts.ListenToRabbitQueue(theQueueName).UseNServiceBusInterop();

                opts.DisableConventionalDiscovery().IncludeType<NServiceBusXmlMessageHandler>();
            }).StartAsync();

        theXmlSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "xml-sender";
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

                #region sample_nservicebus_xml_interop

                opts.PublishMessage<NServiceBusXmlMessage>().ToRabbitQueue(theQueueName)

                    // NServiceBus headers and conventions
                    .UseNServiceBusInterop()

                    // Send the message bodies in the XML format of NServiceBus' XmlSerializer
                    // instead of JSON
                    .DefaultSerializer(new NServiceBusXmlSerializer());

                #endregion

                opts.DisableConventionalDiscovery();
            }).StartAsync();

        theJsonSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "json-sender";
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();
                opts.PublishMessage<NServiceBusXmlMessage>().ToRabbitQueue(theQueueName).UseNServiceBusInterop();
                opts.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theJsonSender.StopAsync();
        await theXmlSender.StopAsync();
        await theListener.StopAsync();
        theJsonSender.Dispose();
        theXmlSender.Dispose();
        theListener.Dispose();
    }

    private async Task<Envelope> sendFrom(IHost sender, NServiceBusXmlMessage message)
    {
        var session = await sender.TrackActivity()
            .AlsoTrack(theListener)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        return session.Received.SingleEnvelope<NServiceBusXmlMessage>();
    }

    [Fact]
    public async Task an_xml_message_goes_out_with_nservicebus_xml_on_the_wire()
    {
        var message = new NServiceBusXmlMessage
        {
            Id = Guid.NewGuid(), Name = "xml", Tags = ["a", "b"], Amount = 12.5m
        };

        var envelope = await sendFrom(theXmlSender, message);

        envelope.ContentType.ShouldBe("text/xml");
        envelope.Headers["NServiceBus.ContentType"].ShouldBe("text/xml");

        var body = Encoding.UTF8.GetString(envelope.Data!);
        body.ShouldStartWith("<?xml version=\"1.0\"?><NServiceBusXmlMessage ");
        body.ShouldContain("xmlns=\"http://tempuri.net/Wolverine.RabbitMQ.Tests\"");
        body.ShouldContain("<Tags><String>a</String><String>b</String></Tags>");

        var received = envelope.Message.ShouldBeOfType<NServiceBusXmlMessage>();
        received.Id.ShouldBe(message.Id);
        received.Name.ShouldBe("xml");
        received.Tags.ShouldBe(["a", "b"]);
        received.Amount.ShouldBe(12.5m);
    }

    [Fact]
    public async Task the_same_listener_still_reads_nservicebus_json()
    {
        var message = new NServiceBusXmlMessage { Id = Guid.NewGuid(), Name = "json", Tags = ["c"], Amount = 1m };

        var envelope = await sendFrom(theJsonSender, message);

        envelope.ContentType.ShouldBe("application/json");
        envelope.Headers["NServiceBus.ContentType"].ShouldBe("application/json");

        var received = envelope.Message.ShouldBeOfType<NServiceBusXmlMessage>();
        received.Id.ShouldBe(message.Id);
        received.Name.ShouldBe("json");
        received.Tags.ShouldBe(["c"]);
    }
}

public class NServiceBusXmlMessage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public List<string> Tags { get; set; } = [];
    public decimal Amount { get; set; }
}

public class NServiceBusXmlMessageHandler
{
    public static void Handle(NServiceBusXmlMessage message)
    {
        // Nothing to do, the tracked session captures the envelope
    }
}
