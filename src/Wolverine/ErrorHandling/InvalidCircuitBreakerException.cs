using System;
using System.Collections.Generic;
using Baseline;

namespace Wolverine.ErrorHandling;

public class InvalidCircuitBreakerException : Exception
{
    public InvalidCircuitBreakerException(IEnumerable<string> messages) : base($"Invalid Circuit Breaker configuration: {messages.Join(", ")}")
    {
    }
}
