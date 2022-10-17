using Amazon.SQS.Model;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsEndpoint : TransportEndpoint<Message, SendMessageBatchRequestEntry>
{
    private readonly AmazonSqsTransport _parent;
    public string QueueName { get; private set; }

    [Obsolete("Get rid of this soon when the Parse() thing goes away")]
    public AmazonSqsEndpoint(AmazonSqsTransport parent)
    {
        _parent = parent;
    }

    public AmazonSqsEndpoint(string queueName, AmazonSqsTransport parent)
    {
        _parent = parent;
        QueueName = queueName;
        Uri = $"sqs://{queueName}".ToUri();
    }

    public override Uri Uri { get; }
    public override void Parse(Uri uri)
    {
        throw new NotSupportedException();
    }
    
    // Set by the AmazonSqsTransport parent
    internal string QueueUrl { get; set; }

    public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
    {
        return new SqsListener(runtime.Logger, this, _parent, receiver);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        var protocol = new SqsSenderProtocol(this, _parent.Client, runtime.Logger);
        return new BatchedSender(Uri, protocol, runtime.Cancellation,
            runtime.Logger);
    }

    protected override void writeOutgoingHeader(SendMessageBatchRequestEntry outgoing, string key, string value)
    {
        outgoing.MessageAttributes[key] = new MessageAttributeValue { StringValue = value };
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
}