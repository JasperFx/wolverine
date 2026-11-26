using System.Diagnostics;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class receiver_persisted_retry
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task recovered_retry_executes_once_even_if_the_first_delivery_is_still_settling(bool overlap)
    {
        var runtime = new MockWolverineRuntime();
        var endpoint = new StubEndpoint("retry", new StubTransport())
        {
            Mode = EndpointMode.Inline,
            InMemoryIdempotency = new InMemoryIdempotencySettings()
        };
        endpoint.Compile(runtime);
        var broker = Substitute.For<IListener>();
        broker.Address.Returns(endpoint.Uri);
        broker.RequiresAckAfterPersistedRetry.Returns(true);
        var recovery = Substitute.For<IRecoveredEnvelopeListener>();
        recovery.Address.Returns(endpoint.Uri);

        var pipeline = Substitute.For<IHandlerPipeline>();
        using var receiver = new InlineReceiver(endpoint, runtime, pipeline);
        var original = ObjectMother.Envelope();
        Envelope redelivery() => new() { Id = original.Id, Message = original.Message };

        var invocations = 0;
        var successes = 0;
        pipeline.InvokeAsync(Arg.Any<Envelope>(), Arg.Any<IChannelCallback>(), Arg.Any<Activity>())
            .Returns(async call =>
            {
                var envelope = call.Arg<Envelope>();
                var context = new MessageContext(runtime);
                context.ReadEnvelope(envelope, call.Arg<IChannelCallback>());
                if (++invocations == 1)
                {
                    await context.ReScheduleAsync(DateTimeOffset.UtcNow);
                    if (overlap) await receiver.ReceivedAsync(recovery, redelivery());
                    return;
                }

                await context.CompleteAsync();
                successes++;
            });

        await receiver.ReceivedAsync(broker, original);
        if (!overlap) await receiver.ReceivedAsync(recovery, redelivery());
        // A duplicate after successful recovery must still be suppressed.
        await receiver.ReceivedAsync(broker, redelivery());

        invocations.ShouldBe(2);
        successes.ShouldBe(1);
        await broker.Received(1).CompleteAsync(Arg.Is<Envelope>(e => ReferenceEquals(e, original)));
        await recovery.Received(1).CompleteAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task native_ack_receiver_executes_inbox_recovery_for_an_already_settled_broker_id()
    {
        var runtime = new MockWolverineRuntime();
        var endpoint = new NativeAckStubEndpoint("retry", new StubTransport())
        {
            Mode = EndpointMode.NativeAck,
            InMemoryIdempotency = new InMemoryIdempotencySettings()
        };
        endpoint.Compile(runtime);
        var envelope = ObjectMother.Envelope();
        envelope.Destination = endpoint.Uri;
        var guard = endpoint.IdempotencyGuard!;
        guard.TryBeginProcessing(envelope).ShouldBeTrue();
        guard.MarkProcessed(envelope);

        var recovered = Substitute.For<IRecoveredEnvelopeListener>();
        recovered.Address.Returns(endpoint.Uri);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recovered.CompleteAsync(envelope).Returns(_ =>
        {
            settled.TrySetResult();
            return ValueTask.CompletedTask;
        });

        var pipeline = Substitute.For<IHandlerPipeline>();
        pipeline.InvokeAsync(envelope, recovered).Returns(async _ =>
        {
            var context = new MessageContext(runtime);
            context.ReadEnvelope(envelope, recovered);
            await context.CompleteAsync();
        });
        using var receiver = new NativeAckReceiver(endpoint, runtime, pipeline);
        await receiver.ReceivedAsync(recovered, envelope);
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await pipeline.Received(1).InvokeAsync(envelope, recovered);
    }
}
