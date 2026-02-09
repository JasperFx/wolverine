using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.AmazonSns.Internal;

namespace Wolverine.AmazonSns.Tests.Internal;

public class OutgoingSnsBatchTests
{
    private readonly AmazonSnsTopic _topic;

    public OutgoingSnsBatchTests()
    {
        _topic = new AmazonSnsTopic("test-topic", new AmazonSnsTransport())
        {
            TopicArn = "arn:aws:sns:us-east-1:000000000000:test-topic",
            Mapper = new StubMapper()
        };
    }

    [Fact]
    public void batch_entries_include_message_attributes_from_mapper()
    {
        _topic.Mapper = new StubMapper(("my-attr", "my-value"));

        var envelope = new Envelope { Id = Guid.NewGuid() };
        var batch = new OutgoingSnsBatch(_topic, NullLogger.Instance, [envelope]);

        var entry = batch.Request.PublishBatchRequestEntries.ShouldHaveSingleItem();
        entry.MessageAttributes.ShouldNotBeNull();
        entry.MessageAttributes["my-attr"].StringValue.ShouldBe("my-value");
    }

    [Fact]
    public void request_is_constructed_even_when_envelope_fails_mapping()
    {
        _topic.Mapper = new ThrowingMapper();

        var envelope = new Envelope { Id = Guid.NewGuid() };
        var batch = new OutgoingSnsBatch(_topic, NullLogger.Instance, [envelope]);

        batch.Request.ShouldNotBeNull();
        batch.Request.TopicArn.ShouldBe(_topic.TopicArn);
        batch.Request.PublishBatchRequestEntries.ShouldBeEmpty();
    }

    [Fact]
    public void mapping_failure_does_not_block_other_entries()
    {
        _topic.Mapper = new FailOnSecondMapper();

        var envelopes = new[]
        {
            new Envelope { Id = Guid.NewGuid() },
            new Envelope { Id = Guid.NewGuid() },
            new Envelope { Id = Guid.NewGuid() }
        };

        var batch = new OutgoingSnsBatch(_topic, NullLogger.Instance, envelopes);

        batch.Request.PublishBatchRequestEntries.Count.ShouldBe(2);
    }

    private class StubMapper : ISnsEnvelopeMapper
    {
        private readonly (string Key, string Value)[] _attributes;

        public StubMapper(params (string Key, string Value)[] attributes) => _attributes = attributes;

        public string BuildMessageBody(Envelope envelope) => "body";

        public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
        {
            foreach (var (key, value) in _attributes)
                yield return new(key, new MessageAttributeValue { StringValue = value, DataType = "String" });
        }

        public void ReadEnvelopeData(Envelope envelope, string messageBody,
            IDictionary<string, MessageAttributeValue> attributes) { }
    }

    private class ThrowingMapper : ISnsEnvelopeMapper
    {
        public string BuildMessageBody(Envelope envelope) => throw new Exception("mapping failure");
        public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope) => [];
        public void ReadEnvelopeData(Envelope envelope, string messageBody,
            IDictionary<string, MessageAttributeValue> attributes) { }
    }

    private class FailOnSecondMapper : ISnsEnvelopeMapper
    {
        private int _callCount;

        public string BuildMessageBody(Envelope envelope)
        {
            if (++_callCount == 2) throw new Exception("mapping failure");
            return "body";
        }

        public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope) => [];
        public void ReadEnvelopeData(Envelope envelope, string messageBody,
            IDictionary<string, MessageAttributeValue> attributes) { }
    }
}
