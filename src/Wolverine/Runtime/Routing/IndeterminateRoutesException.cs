using System;
using JasperFx.CodeGeneration;

namespace Wolverine.Runtime.Routing;

public class IndeterminateRoutesException : Exception
{
    public IndeterminateRoutesException(Envelope? envelope) : base($"Could not determine any valid routes for {envelope}")
    {
    }

    public IndeterminateRoutesException(Type messageType, string? message = null) : base(
        $"Could not determine any valid subscribers or local handlers for message type {messageType.FullNameInCode()}{Environment.NewLine}{message}")
    {
    }
}