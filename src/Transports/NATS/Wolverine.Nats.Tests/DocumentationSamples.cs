using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;

namespace Wolverine.Nats.Tests;

public class DocumentationSamples
{
    public static async Task use_interop_with_a_custom_mapper()
    {
        #region sample_nats_use_interop

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseNats("nats://localhost:4222");

                // A completely custom mapper for everything received on this subject
                opts.ListenToNatsSubject("orders.incoming")
                    .UseInterop(new KindHeaderNatsMapper());

                // Or built lazily from the running application and the endpoint
                opts.PublishAllMessages().ToNatsSubject("orders.outgoing")
                    .UseInterop((runtime, endpoint) => new KindHeaderNatsMapper());

                // Or keep Wolverine's own mapping, and just change how some of it
                // goes over the wire
                opts.ListenToNatsSubject("invoices")
                    .UseJetStream("INVOICES")
                    .UseInterop((endpoint, mapper) =>
                    {
                        mapper.MapPropertyToHeader(x => x.CorrelationId!, "x-correlation-id");
                    });
            }).StartAsync();

        #endregion
    }

    public static async Task interop_with_cloud_events()
    {
        #region sample_nats_interop_with_cloudevents

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseNats("nats://localhost:4222");

                // CloudEvents needs a message type alias to know what the
                // "type" of an incoming CloudEvent means
                opts.RegisterMessageType(typeof(NatsInteropColor), "nats.interop.color");

                // Read CloudEvents JSON from a core NATS subject...
                opts.ListenToNatsSubject("colors")

                    // Just note that you *can* override the STJ serialization
                    // settings for messages coming in with the CloudEvents
                    // wrapper
                    .InteropWithCloudEvents(new JsonSerializerOptions());

                // ...or from a JetStream stream
                opts.ListenToNatsSubject("colors.durable")
                    .UseJetStream("COLORS")
                    .InteropWithCloudEvents();

                // And publish CloudEvents JSON to a subject
                opts.PublishMessage<NatsInteropColor>().ToNatsSubject("colors.outgoing")
                    .InteropWithCloudEvents();
            }).StartAsync();

        #endregion
    }

    public static async Task raw_json()
    {
        #region sample_nats_raw_json

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseNats("nats://localhost:4222");

                opts.ListenToNatsSubject("colors.raw")

                    // You do have to tell Wolverine what the message type
                    // is that you'll receive here so that it can deserialize the
                    // incoming data
                    .ReceiveRawJson<NatsInteropColor>();

                opts.PublishMessage<NatsInteropColor>().ToNatsSubject("colors.raw.outgoing")

                    // Just publish the outgoing information as pure JSON
                    // and no other Wolverine metadata
                    .PublishRawJson(new JsonSerializerOptions());
            }).StartAsync();

        #endregion
    }
}

#region sample_nats_custom_envelope_mapper

/// <summary>
/// For an upstream system that puts plain JSON on a NATS subject and names
/// the message in a header of its own
/// </summary>
public class KindHeaderNatsMapper : INatsEnvelopeMapper
{
    public const string KindHeader = "x-message-kind";

    public void MapEnvelopeToOutgoing(Envelope envelope, NatsHeaders outgoing)
    {
        // The message body is Envelope.Data, written by the endpoint's
        // serializer. The mapper only decides the headers
        if (envelope.MessageType != null)
        {
            outgoing[KindHeader] = envelope.MessageType;
        }

        if (envelope.CorrelationId != null)
        {
            outgoing["x-correlation-id"] = envelope.CorrelationId;
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, NatsMsg<byte[]> incoming)
    {
        envelope.Data = incoming.Data;
        envelope.ContentType = "application/json";

        if (incoming.Headers == null) return;

        // Wolverine finds the message type by this name or by an alias
        // registered with opts.RegisterMessageType()
        if (incoming.Headers.TryGetValue(KindHeader, out var kind))
        {
            envelope.MessageType = kind.ToString();
        }

        if (incoming.Headers.TryGetValue("x-correlation-id", out var correlationId))
        {
            envelope.CorrelationId = correlationId.ToString();
        }

        // Keep everything else for the handlers to see
        foreach (var header in incoming.Headers)
        {
            envelope.Headers[header.Key] = header.Value.ToString();
        }
    }
}

#endregion
