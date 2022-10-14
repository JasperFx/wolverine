using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class SqsListener : IListener
{
    private readonly ILogger _logger;
    private readonly AmazonSqsEndpoint _endpoint;
    private readonly AmazonSqsTransport _transport;
    private readonly IReceiver _receiver;

    private readonly string _queueUrl;
    private readonly IAmazonSQS _sqs;

    public SqsListener(ILogger logger, AmazonSqsEndpoint endpoint, AmazonSqsTransport transport, IReceiver receiver)
    {
        // TODO -- really needs an ISqsClient and a queue url. Not really needing the parent
        
        _logger = logger;
        _endpoint = endpoint;
        _transport = transport;
        _receiver = receiver;
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await CompleteAsync(e.SqsMessage);
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await DeferAsync(e.SqsMessage);
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Uri Address => _endpoint.Uri;
    public ValueTask StopAsync()
    {
        throw new NotImplementedException();
    }

    public Task CompleteAsync(Message sqsMessage)
    {
        // TODO -- harden this like crazy
        return _sqs.DeleteMessageAsync(_queueUrl, sqsMessage.ReceiptHandle);
    }

    public Task DeferAsync(Message sqsMessage)
    {
        // TODO -- harden this like crazy
        // TODO -- the visibility timeout needs to be configurable by timeout
        return _sqs.ChangeMessageVisibilityAsync(_queueUrl, sqsMessage.ReceiptHandle, 1000);
    }
}