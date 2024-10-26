namespace Wolverine.Pubsub;

public class WolverinePubsubTransportException : Exception
{
    public WolverinePubsubTransportException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}