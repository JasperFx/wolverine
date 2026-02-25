using System;
using CoreTests.Runtime;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.ErrorHandling;

public class PauseListenerContinuationTests
{
    [Fact]
    public async Task execute_with_null_envelope_does_not_throw()
    {
        var continuation = new PauseListenerContinuation(TimeSpan.FromSeconds(10));
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns((Envelope?)null);

        var runtime = new MockWolverineRuntime();

        await continuation.ExecuteAsync(lifecycle, runtime, DateTimeOffset.UtcNow, null);
    }

    [Fact]
    public async Task execute_with_missing_listener_uses_destination_lookup_without_throwing()
    {
        var continuation = new PauseListenerContinuation(TimeSpan.FromSeconds(10));
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var envelope = ObjectMother.Envelope();
        var destination = new Uri("rabbitmq://queue/paused");
        envelope.Destination = destination;
        envelope.Listener = null;
        lifecycle.Envelope.Returns(envelope);

        var runtime = new MockWolverineRuntime();
        runtime.Endpoints.FindListeningAgent(destination).Returns((IListenerCircuit?)null);

        await continuation.ExecuteAsync(lifecycle, runtime, DateTimeOffset.UtcNow, null);

        runtime.Endpoints.Received(1).FindListeningAgent(destination);
    }
}
