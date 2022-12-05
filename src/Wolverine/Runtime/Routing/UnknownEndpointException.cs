using System;

namespace Wolverine.Runtime.Routing;

public class UnknownEndpointException : Exception
{
    public UnknownEndpointException(string? message) : base(message)
    {
    }
}