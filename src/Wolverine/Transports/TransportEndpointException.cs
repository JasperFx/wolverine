using System;

namespace Wolverine.Transports;

public class TransportEndpointException : Exception
{
    public TransportEndpointException(Uri? uri, string message, Exception innerException) : base(
        $"Error with endpoint '{uri}': {message}", innerException)
    {
    }
}
