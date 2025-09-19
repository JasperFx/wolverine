using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
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
        theRuntime.Options.ServiceName = "MyService";

        var original = ObjectMother.Envelope();
        original.Id = Guid.NewGuid();
        original.CorrelationId = Guid.NewGuid().ToString();
        original.ConversationId = Guid.NewGuid();
        original.TenantId = "some tenant";
        original.SagaId = "some saga";

        var context = new MessageContext(theRuntime);
        context.ReadEnvelope(original, InvocationCallback.Instance);
        theContext = context.As<MessageContext>();

        theEnvelope = ObjectMother.Envelope();
    }

    [Fact]
    public async Task reject_side_effect_as_cascading_message()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await theContext.EnqueueCascadingAsync(Substitute.For<ISideEffect>());
        });
    }

    [Fact]
    public void track_envelope_correlation()
    {
        using var activity = new Activity("DoWork");
        activity.Start();

        theContext.TrackEnvelopeCorrelation(theEnvelope, activity);

        theEnvelope.TenantId.ShouldBe(theContext.TenantId);

        theEnvelope.SagaId.ShouldBe("some saga");
        theEnvelope.ConversationId.ShouldBe(theContext.Envelope.ConversationId);

        theEnvelope.Source.ShouldBe("MyService");
        theEnvelope.CorrelationId.ShouldBe(theContext.CorrelationId);

        activity.Id.ShouldNotBeNull();
        theEnvelope.ParentId.ShouldBe(activity.Id);
    }

    [Fact]
    public void reads_tenant_id_from_envelope()
    {
        theContext.TenantId.ShouldBe("some tenant");
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

        var outbox = Substitute.For<IEnvelopeTransaction>();
        await context.EnlistInOutboxAsync(outbox);

        await context.PublishAsync(new Message1());
        await context.ScheduleAsync(new Message2(), 1.Hours());

        context.Outstanding.Any().ShouldBeTrue();

        await context.ClearAllAsync();

        context.Outstanding.Any().ShouldBeFalse();

        await outbox.Received().RollbackAsync();

        context.Transaction.ShouldBeNull();
    }

    [Fact]
    public async Task reschedule_without_native_scheduling()
    {
        var callback = Substitute.For<IChannelCallback>();
        var scheduledTime = DateTime.Today.AddHours(8);

        theContext.ReadEnvelope(theEnvelope, callback);

        await theContext.ReScheduleAsync(scheduledTime);

        theEnvelope.ScheduledTime.ShouldBe(scheduledTime);

        await theContext.Storage.Inbox.Received().RescheduleExistingEnvelopeForRetryAsync(theEnvelope);
    }

    [Fact]
    public async Task reschedule_with_native_scheduling()
    {
        var callback = Substitute.For<IChannelCallback, ISupportNativeScheduling>();
        var scheduledTime = DateTime.Today.AddHours(8);

        theContext.ReadEnvelope(theEnvelope, callback);

        await theContext.ReScheduleAsync(scheduledTime);

        theEnvelope.ScheduledTime.ShouldBe(scheduledTime);

        await theContext.Storage.Inbox.DidNotReceive().RescheduleExistingEnvelopeForRetryAsync(theEnvelope);
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

        await theRuntime.Storage.Inbox.Received()
            .MoveToDeadLetterStorageAsync(theEnvelope, exception);
    }

    [Fact]
    public async Task move_to_dead_letter_queue_with_native_dead_letter()
    {
        var callback = Substitute.For<IChannelCallback, ISupportDeadLetterQueue>();
        callback.As<ISupportDeadLetterQueue>().NativeDeadLetterQueueEnabled.Returns(true);

        theContext.ReadEnvelope(theEnvelope, callback);

        var exception = new Exception();

        await theContext.MoveToDeadLetterQueueAsync(exception);

        await callback.As<ISupportDeadLetterQueue>().Received()
            .MoveToErrorsAsync(theEnvelope, exception);

        await theRuntime.Storage.Inbox.DidNotReceive()
            .MoveToDeadLetterStorageAsync(theEnvelope, exception);
    }

    [Fact]
    public async Task move_to_dead_letter_queue_without_native_dead_letter_if_native_dlq_is_disabled()
    {
        var callback = Substitute.For<IChannelCallback, ISupportDeadLetterQueue>();
        callback.As<ISupportDeadLetterQueue>().NativeDeadLetterQueueEnabled.Returns(false);

        theContext.ReadEnvelope(theEnvelope, callback);

        var exception = new Exception();

        await theContext.MoveToDeadLetterQueueAsync(exception);

        await theRuntime.Storage.Inbox.Received()
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