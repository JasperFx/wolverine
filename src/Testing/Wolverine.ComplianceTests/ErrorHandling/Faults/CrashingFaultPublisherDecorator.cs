using System.Diagnostics;
using Wolverine.ErrorHandling;

namespace Wolverine.ComplianceTests.ErrorHandling.Faults;

internal sealed class CrashingFaultPublisherDecorator : IFaultPublisher
{
    private readonly IFaultPublisher _inner;

    public CrashingFaultPublisherDecorator(IFaultPublisher inner) => _inner = inner;

    public async ValueTask PublishIfEnabledAsync(
        IEnvelopeLifecycle lifecycle,
        Exception exception,
        FaultTrigger trigger,
        Activity? activity)
    {
        await _inner.PublishIfEnabledAsync(lifecycle, exception, trigger, activity);
        throw new SimulatedCrashException("test: simulating crash after Fault<T> publish");
    }
}

internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(string message) : base(message) { }
}
