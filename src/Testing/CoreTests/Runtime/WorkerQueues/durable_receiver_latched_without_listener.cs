using System;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class durable_receiver_latched_without_listener
{
    [Fact]
    public async Task latched_receiver_throws_when_envelope_listener_is_missing()
    {
        var runtime = new MockWolverineRuntime();
        var pipeline = Substitute.For<IHandlerPipeline>();
        var endpoint = new StubEndpoint("one", new StubTransport());
        var receiver = new DurableReceiver(endpoint, runtime, pipeline);
        receiver.Latch();

        var envelope = ObjectMother.Envelope();
        var listener = Substitute.For<IListener>();

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await receiver.ReceivedAsync(listener, envelope));

        if (exception is AggregateException aggregate)
        {
            Assert.Contains(aggregate.InnerExceptions, inner => inner is NullReferenceException);
        }
        else
        {
            Assert.IsType<NullReferenceException>(exception);
        }

        envelope.Listener.ShouldBeNull();
    }
}
