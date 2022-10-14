using Amazon.SQS.Model;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsEnvelope : Envelope
{
    private readonly SqsListener _listener;

    public AmazonSqsEnvelope(SqsListener listener, Message message)
    {
        _listener = listener;
        SqsMessage = message;
    }

    public Message SqsMessage { get; }

    public Task CompleteAsync()
    {
        return _listener.CompleteAsync(SqsMessage);
    }

    public Task DeferAsync()
    {
        return _listener.DeferAsync(SqsMessage);
    }
}