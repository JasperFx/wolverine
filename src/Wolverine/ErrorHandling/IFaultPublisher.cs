using System.Diagnostics;

namespace Wolverine.ErrorHandling;

internal interface IFaultPublisher
{
    /// <summary>
    /// Publish a Fault&lt;T&gt; for the supplied envelope if the configuration says we
    /// should. No-op when the resolved mode is None or the trigger is Discarded
    /// without an explicit opt-in.
    ///
    /// MUST NOT throw — internal failures are caught, logged and counted.
    /// </summary>
    ValueTask PublishIfEnabledAsync(
        IEnvelopeLifecycle lifecycle,
        Exception exception,
        FaultTrigger trigger,
        Activity? activity);
}
