using System;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Plugin point for creating continuations based on failures
/// </summary>
public interface IContinuationSource
{
    string Description { get; }
    IContinuation Build(Exception ex, Envelope envelope);
}
