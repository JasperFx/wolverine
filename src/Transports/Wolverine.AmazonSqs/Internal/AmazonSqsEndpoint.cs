using Amazon.SQS.Model;
using Baseline;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsEndpoint : TransportEndpoint<Message, SendMessageBatchRequestEntry>
{
    private readonly AmazonSqsTransport _parent;
    public string QueueName { get; private set; }

    [Obsolete("Get rid of this soon when the Parse() thing goes away")]
    internal AmazonSqsEndpoint(AmazonSqsTransport parent)
    {
        _parent = parent;
    }

    internal AmazonSqsEndpoint(string queueName, AmazonSqsTransport parent)
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
    internal string QueueUrl { get; private set; }

    internal async ValueTask InitializeAsync()
    {
        // TODO -- allow for config on endpoint?
        if (_parent.AutoProvision)
        {
            // TODO -- use the configuration here for FIFO or Standard
            var response = await _parent.Client.CreateQueueAsync(QueueName);

            QueueUrl = response.QueueUrl;
        }

        if (QueueUrl.IsEmpty())
        {
            var response = await _parent.Client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        // TODO -- allow for endpoint by endpoint variance
        if (_parent.AutoPurgeOnStartup)
        {
            await _parent.Client.PurgeQueueAsync(QueueUrl);
        }
    }

    public override IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver)
    {
        assertReady();

        return new SqsListener(runtime.Logger, this, _parent, receiver);
    }

    private void assertReady()
    {
        if (QueueUrl.IsEmpty()) throw new InvalidOperationException("This endpoint has not yet been initialized");

        if (_parent.Client == null)
            throw new InvalidOperationException("The parent transport has not yet been initialized");
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        assertReady();
        
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