using Wolverine.Logging;

namespace Wolverine.Runtime;

/// <summary>
///     CritterWatch GH-907 / wolverine#3774. Completion continuations (success, no-handler,
///     error-queue, discard) historically reported through the runtime's global tracker, which
///     bypassed the metrics-silent tracker the executor resolved for system traffic — so a
///     system message's completion still recorded wolverine-messages-succeeded and
///     wolverine-effective-time (and, in the publishing modes, still fed the CritterWatch
///     accumulator's effective-time rollup: the CritterWatch#880 producer).
///
///     <para>The diversion is deliberately narrow: ONLY the metrics-silent tracker is honored
///     here. Every other resolved tracker (Direct/Hybrid publishing) keeps the historical
///     runtime-global reporting, because those trackers' completion methods post to their own
///     accumulator sink AND delegate to the runtime — routing completions through them would
///     double-count application traffic. The single type test below is on the completion path,
///     not the per-envelope hot path.</para>
/// </summary>
internal static class CompletionTrackerExtensions
{
    internal static IMessageTracker CompletionTrackerFor(this IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime)
    {
        if (lifecycle is MessageContext { Tracker: WolverineRuntime.SystemTrafficMessageTracker silent })
        {
            return silent;
        }

        return runtime.MessageTracking;
    }
}
