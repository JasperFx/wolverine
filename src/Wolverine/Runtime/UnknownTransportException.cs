namespace Wolverine.Runtime;

public class UnknownTransportException : Exception
{
    public UnknownTransportException(string message) : base(message)
    {
    }
}