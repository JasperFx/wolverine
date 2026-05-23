using Amazon.SQS.Model;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs;

/// <summary>
/// Pluggable strategy for reading and writing data to Amazon SQS queues
/// </summary>
public interface ISqsEnvelopeMapper
{
    string BuildMessageBody(Envelope envelope);

    IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope);

    void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes);

    /// <summary>
    /// Determine the SQS <c>MessageGroupId</c> for an outgoing message. This is applied to FIFO
    /// queues and, when <c>EnableFairQueueMessageGroups()</c> is set, to standard queues to opt into
    /// SQS fair queues. Return <c>null</c> to leave <c>MessageGroupId</c> unset. The default maps
    /// <see cref="Envelope.GroupId"/>; override to source the group id from a header, tenant id, etc.
    /// </summary>
    string? DetermineGroupId(Envelope envelope) => envelope.GroupId;
}

public class DefaultSqsEnvelopeMapper : ISqsEnvelopeMapper
{
    public string BuildMessageBody(Envelope envelope)
    {
        var data = EnvelopeSerializer.Serialize(envelope);
        return Convert.ToBase64String(data);
    }

    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        yield return new KeyValuePair<string, MessageAttributeValue>(TransportConstants.ProtocolVersion,
            new MessageAttributeValue { StringValue = "1.0", DataType = "String" });
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody,
        IDictionary<string, MessageAttributeValue> attributes)
    {
        var buffer = Convert.FromBase64String(messageBody);
        EnvelopeSerializer.ReadEnvelopeData(envelope, buffer);
    }

    public string? DetermineGroupId(Envelope envelope) => envelope.GroupId;
}