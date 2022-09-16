using System;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.Dates;
using CoreTests.Messaging;
using NSubstitute;
using Shouldly;
using TestingSupport;
using TestMessages;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Runtime;

public class MessageContextTests
{
    private readonly MessageContext theContext;
    private readonly Envelope theEnvelope;
    private readonly MockWolverineRuntime theRuntime;

    public MessageContextTests()
    {
        theRuntime = new MockWolverineRuntime();

        var original = ObjectMother.Envelope();
        original.Id = Guid.NewGuid();
        original.CorrelationId = Guid.NewGuid().ToString();

        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(original, InvocationCallback.Instance);
        theContext = context.As<MessageContext>();

        theEnvelope = ObjectMother.Envelope();
    }

    [Fact]
    public async Task clear_all_cleans_out_outstanding_messages()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());
        });

        var context = new MessageContext(host.GetRuntime());

        context.ReadEnvelope(theEnvelope, Substitute.For<IChannelCallback>());

        var outbox = Substitute.For<IEnvelopeOutbox>();
        await context.EnlistInOutboxAsync(outbox);

        await context.PublishAsync(new Message1());
        await context.ScheduleAsync(new Message2(), 1.Hours());

        context.Outstanding.Any().ShouldBeTrue();

        await context.ClearAllAsync();

        context.Outstanding.Any().ShouldBeFalse();

        await outbox.Received().RollbackAsync();

        context.Outbox.ShouldBeNull();
    }

    [Fact]
    public async Task reschedule_without_native_scheduling()
    {
        var callback = Substitute.For<IChannelCallback>();
        var scheduledTime = DateTime.Today.AddHours(8);

        theContext.ReadEnvelope(theEnvelope, callback);

        await theContext.ReScheduleAsync(scheduledTime);

        theEnvelope.ScheduledTime.ShouldBe(scheduledTime);

        await theContext.Persistence.Received().ScheduleJobAsync(theEnvelope);
    }

    [Fact]
    public async Task reschedule_with_native_scheduling()
    {
        var callback = Substitute.For<IChannelCallback, ISupportNativeScheduling>();
        var scheduledTime = DateTime.Today.AddHours(8);

        theContext.ReadEnvelope(theEnvelope, callback);

        await theContext.ReScheduleAsync(scheduledTime);

        theEnvelope.ScheduledTime.ShouldBe(scheduledTime);

        await theContext.Persistence.DidNotReceive().ScheduleJobAsync(theEnvelope);
        await callback.As<ISupportNativeScheduling>().Received()
            .MoveToScheduledUntilAsync(theEnvelope, scheduledTime);
    }

    [Fact]
    public async Task move_to_dead_letter_queue_without_native_dead_letter()
    {
        var callback = Substitute.For<IChannelCallback>();

        theContext.ReadEnvelope(theEnvelope, callback);

        var exception = new Exception();

        await theContext.MoveToDeadLetterQueueAsync(exception);

        await theRuntime.Persistence.Received()
            .MoveToDeadLetterStorageAsync(theEnvelope, exception);
    }

    [Fact]
    public async Task move_to_dead_letter_queue_with_native_dead_letter()
    {
        var callback = Substitute.For<IChannelCallback, ISupportDeadLetterQueue>();

        theContext.ReadEnvelope(theEnvelope, callback);

        var exception = new Exception();

        await theContext.MoveToDeadLetterQueueAsync(exception);

        await callback.As<ISupportDeadLetterQueue>().Received()
            .MoveToErrorsAsync(theEnvelope, exception);

        await theRuntime.Persistence.DidNotReceive()
            .MoveToDeadLetterStorageAsync(theEnvelope, exception);
    }

    [Fact]
    public void correlation_id_should_be_same_as_original_envelope()
    {
        theContext.CorrelationId.ShouldBe(theContext.Envelope.CorrelationId);
    }

    [Fact]
    public void new_context_gets_a_non_empty_correlation_id()
    {
        theRuntime.NewContext().CorrelationId.ShouldNotBeNull();
    }
}
