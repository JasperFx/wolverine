namespace Wolverine.AmazonSqs;

public class WolverineSqsTransportException : Exception
{
    public WolverineSqsTransportException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}