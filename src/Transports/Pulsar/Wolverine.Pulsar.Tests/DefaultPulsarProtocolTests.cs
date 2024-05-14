﻿using System.Buffers;
using System.Reflection;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Internal.PulsarApi;
using JasperFx.Core;
using NSubstitute;
using Shouldly;
using TestingSupport;
using Wolverine.Runtime;
using Xunit;
using MessageMetadata = DotPulsar.MessageMetadata;

namespace Wolverine.Pulsar.Tests;

internal class StubMessage : IMessage<ReadOnlySequence<byte>>
{
    public MessageId MessageId { get; }
    public ReadOnlySequence<byte> Data { get; }
    public string ProducerName { get; }
    public byte[]? SchemaVersion { get; }
    public ulong SequenceId { get; }
    public uint RedeliveryCount { get; }
    public bool HasEventTime { get; }
    public ulong EventTime { get; }
    public DateTime EventTimeAsDateTime { get; }
    public DateTimeOffset EventTimeAsDateTimeOffset { get; }
    public bool HasBase64EncodedKey { get; }
    public bool HasKey { get; }
    public string? Key { get; }
    public byte[]? KeyBytes { get; }
    public bool HasOrderingKey { get; }
    public byte[]? OrderingKey { get; }
    public ulong PublishTime { get; }
    public DateTime PublishTimeAsDateTime { get; }
    public DateTimeOffset PublishTimeAsDateTimeOffset { get; }
    public IReadOnlyDictionary<string, string> Properties { get; set; }

    public ReadOnlySequence<byte> Value()
    {
        throw new NotImplementedException();
    }
}

public class DefaultPulsarProtocolTests
{
    private readonly Lazy<Envelope> _mapped;

    private readonly Envelope theOriginal = new()
    {
        Id = Guid.NewGuid()
    };

    public DefaultPulsarProtocolTests()
    {
        _mapped = new Lazy<Envelope>(() =>
        {
            var endpoint = new PulsarEndpoint("pulsar://persistent/public/default/one".ToUri(), new PulsarTransport());
            var runtime = Substitute.For<IWolverineRuntime>();

            var mapper = endpoint.BuildMapper(runtime);
            var metadata = new MessageMetadata();

            mapper.MapEnvelopeToOutgoing(theOriginal, metadata);

            var prop1 = typeof(MessageMetadata).GetProperty("Metadata",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var internalMetadata = prop1.GetValue(metadata);
            var prop2 = internalMetadata.GetType().GetProperty("Properties");
            var values = (List<KeyValue>)prop2.GetValue(internalMetadata);

            var properties = new Dictionary<string, string>();
            foreach (var pair in values) properties[pair.Key] = pair.Value;

            var message = new StubMessage { Properties = properties };

            var envelope = new PulsarEnvelope(message);

            mapper.MapIncomingToEnvelope(envelope, message);

            return envelope;
        });
    }

    private Envelope theEnvelope => _mapped.Value;

    [Fact]
    public void accepted_types()
    {
        theOriginal.AcceptedContentTypes = ["text/json", "text/xml"];
        theEnvelope.AcceptedContentTypes
            .ShouldHaveTheSameElementsAs(theOriginal.AcceptedContentTypes);
    }

    [Fact]
    public void ack_requestd_true()
    {
        theOriginal.AckRequested = true;
        theEnvelope.AckRequested.ShouldBeTrue();
    }

    [Fact]
    public void ack_requested_false()
    {
        theOriginal.AckRequested = false;
        theEnvelope.AckRequested.ShouldBeFalse();
    }

    [Fact]
    public void is_response_true()
    {
        theOriginal.IsResponse = true;
        theEnvelope.IsResponse.ShouldBeTrue();
    }

    [Fact]
    public void is_response_false()
    {
        theOriginal.IsResponse = false;
        theEnvelope.IsResponse.ShouldBeFalse();
    }

    [Fact]
    public void content_type()
    {
        theOriginal.ContentType = EnvelopeConstants.JsonContentType;
        theEnvelope.ContentType.ShouldBe(theOriginal.ContentType);
    }

    [Fact]
    public void deliver_by_value()
    {
        theOriginal.DeliverBy = DateTimeOffset.Now;
        theEnvelope.DeliverBy.HasValue.ShouldBeTrue();


        theEnvelope.DeliverBy.Value.Subtract(theOriginal.DeliverBy.Value)
            .TotalSeconds.ShouldBeLessThan(5);
    }

    [Fact]
    public void id()
    {
        theEnvelope.Id.ShouldBe(theOriginal.Id);
    }

    [Fact]
    public void message_type()
    {
        theOriginal.MessageType = "somemessagetype";
        theEnvelope.MessageType.ShouldBe(theOriginal.MessageType);
    }

    [Fact]
    public void original_id()
    {
        theOriginal.CorrelationId = Guid.NewGuid().ToString();
        theEnvelope.CorrelationId.ShouldBe(theOriginal.CorrelationId);
    }

    [Fact]
    public void other_random_headers()
    {
        theOriginal.Headers.Add("color", "blue");
        theOriginal.Headers.Add("direction", "north");

        theEnvelope.Headers["color"].ShouldBe("blue");
        theEnvelope.Headers["direction"].ShouldBe("north");
    }

    [Fact]
    public void conversation_id()
    {
        theOriginal.ConversationId = Guid.NewGuid();
        theEnvelope.ConversationId.ShouldBe(theOriginal.ConversationId);
    }

    [Fact]
    public void parent_id()
    {
        theOriginal.ParentId = Guid.NewGuid().ToString();
        theEnvelope.ParentId.ShouldBe(theOriginal.ParentId);
    }

    [Fact]
    public void attempts()
    {
        theOriginal.Attempts = 1;
        theEnvelope.Attempts.ShouldBe(1);
    }

    [Fact]
    public void reply_requested()
    {
        theOriginal.ReplyRequested = "somemessagetype";
        theEnvelope.ReplyRequested.ShouldBe("somemessagetype");
    }

    [Fact]
    public void reply_uri()
    {
        theOriginal.ReplyUri = "tcp://localhost:4444".ToUri();
        theEnvelope.ReplyUri.ShouldBe(theOriginal.ReplyUri);
    }

    [Fact]
    public void saga_id()
    {
        theOriginal.SagaId = Guid.NewGuid().ToString();
        theEnvelope.SagaId.ShouldBe(theOriginal.SagaId);
    }

    [Fact]
    public void source()
    {
        theOriginal.Source = "someapp";
        theEnvelope.Source.ShouldBe(theOriginal.Source);
    }
}