using Amazon.SQS.Model;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsMapper : EnvelopeMapper<Message, SendMessageBatchRequestEntry>
{
    public AmazonSqsMapper(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint, runtime)
    {
    }
    
    protected override void writeOutgoingHeader(SendMessageBatchRequestEntry outgoing, string key, string value)
    {
        outgoing.MessageAttributes[key] = new MessageAttributeValue { StringValue = value, DataType = "String"};
    }

    protected override bool tryReadIncomingHeader(Message incoming, string key, out string? value)
    {
        if (incoming.MessageAttributes.TryGetValue(key, out var attValue))
        {
            value = attValue.StringValue;
            return true;
        }

        value = null;
        return false;
    }

    protected override void writeIncomingHeaders(Message incoming, Envelope envelope)
    {
        foreach (var pair in incoming.MessageAttributes)
            envelope.Headers[pair.Key] = pair.Value.StringValue;
    }
}