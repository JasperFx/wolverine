using System;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Plugin point for creating continuations based on failures
/// </summary>
public interface IContinuationSource
{
    /// <summary>
    /// Description for diagnostics
    /// </summary>
    string Description { get; }
    
    /// <summary>
    /// Build a continuation for a runtime exception and message envelope
    /// </summary>
    /// <param name="ex"></param>
    /// <param name="envelope"></param>
    /// <returns></returns>
    IContinuation Build(Exception ex, Envelope envelope);
}
