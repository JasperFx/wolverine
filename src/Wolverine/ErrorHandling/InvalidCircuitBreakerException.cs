using System;
using System.Collections.Generic;
using Baseline;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Thrown when a specified circuit breaker configuration is invalid
/// </summary>
public class InvalidCircuitBreakerException : Exception
{
    public InvalidCircuitBreakerException(IEnumerable<string> messages) : base($"Invalid Circuit Breaker configuration: {messages.Join(", ")}")
    {
    }
}
