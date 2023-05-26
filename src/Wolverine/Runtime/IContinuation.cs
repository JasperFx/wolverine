using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Wolverine.Runtime;

#region sample_IContinuation

/// <summary>
///     Represents an action to take after processing a message
/// </summary>
public interface IContinuation
{
    /// <summary>
    ///     Post-message handling action
    /// </summary>
    /// <param name="lifecycle"></param>
    /// <param name="runtime"></param>
    /// <param name="now">The current system time</param>
    /// <param name="activity">The current open telemetry activity span for additional tagging or logging</param>
    /// <returns></returns>
    ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity);
}

#endregion