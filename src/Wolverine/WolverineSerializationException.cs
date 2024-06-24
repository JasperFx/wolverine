namespace Wolverine;

public class WolverineSerializationException : Exception
{
    public static WolverineSerializationException FromMissingMessage(Envelope envelope)
    {
        return new WolverineSerializationException($"Cannot ensure data is present when there is no message");
    }
    
    public WolverineSerializationException(string? message) : base(message)
    {
    }

    public WolverineSerializationException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}