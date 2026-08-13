namespace Wolverine.AmazonSqs;

public class WolverineSqsTransportException : Exception
{
    public WolverineSqsTransportException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
///     An envelope that is simply too big for the queue it is bound for, and that the endpoint has not
///     opted into fragmenting. SQS answers an oversized message with a permanent <c>SenderFault</c>, so
///     this is never worth a retry -- which is exactly why it is its own type. The retrying paths
///     (requeue, dead letter forwarding) catch it and give up rather than looping on a send that cannot
///     ever succeed. See GH-3926.
/// </summary>
public class SqsMessageTooLargeException : WolverineSqsTransportException
{
    public SqsMessageTooLargeException(string? message) : base(message, null)
    {
    }
}