namespace Wolverine.AmazonSns;

public class WolverineSnsTransportException : Exception
{
    public WolverineSnsTransportException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
