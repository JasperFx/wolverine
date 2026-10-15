using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

public class ScheduledMessageCancellationTests
{
    [Fact]
    public async Task CancellationAcceptedByBrokerIsReported()
    {
        var sender = Substitute.For<ServiceBusSender>();
        sender.CancelScheduledMessageAsync(12, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await AzureServiceBusScheduledCancellation.CancelAsync(sender, 12, CancellationToken.None);

        result.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        await sender.Received(1).CancelScheduledMessageAsync(12, CancellationToken.None);
    }

    [Fact]
    public async Task MissingSequenceIsReportedAsNoLongerScheduled()
    {
        var sender = Substitute.For<ServiceBusSender>();
        sender.CancelScheduledMessageAsync(12, Arg.Any<CancellationToken>()).Returns<Task>(_ =>
            throw new ServiceBusException("The sequence is no longer scheduled.",
                ServiceBusFailureReason.MessageNotFound));

        var result = await AzureServiceBusScheduledCancellation.CancelAsync(sender, 12, CancellationToken.None);

        result.ShouldBe(ScheduledMessageCancellationStatus.NoLongerScheduled);
    }

    [Fact]
    public async Task BufferedSenderSchedulesCancellableEnvelopesNatively()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        var endpoint = new AzureServiceBusQueue(new AzureServiceBusTransport(), "scheduled-cancellation");
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(123L));
        var callback = Substitute.For<ISenderCallback>();
        var protocol = new AzureServiceBusSenderProtocol(runtime, endpoint, mapper, sender);
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = new Envelope(new ScheduledCancellationMessage()) { ScheduledTime = scheduledTime };
        var registration = new ScheduledMessageCancellationRegistration(envelope.Id);
        envelope.ScheduledCancellation = registration;
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        await protocol.SendBatchAsync(callback, batch);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        await sender.DidNotReceive().SendMessagesAsync(Arg.Any<ServiceBusMessageBatch>(), Arg.Any<CancellationToken>());

        var result = await registration.CancelAsync(CancellationToken.None);
        result.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        await sender.Received(1).CancelScheduledMessageAsync(123L, CancellationToken.None);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        await protocol.SendBatchAsync(callback, batch);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        await sender.DidNotReceive().SendMessagesAsync(Arg.Any<ServiceBusMessageBatch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BufferedSenderFindsCancellationAfterEnvelopeSerialization()
    {
        var runtime = Substitute.For<IWolverineRuntimeInternal>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        var registry = new ScheduledMessageCancellationRegistry();
        runtime.ScheduledMessageCancellations.Returns(registry);
        var endpoint = new AzureServiceBusQueue(new AzureServiceBusTransport(), "scheduled-cancellation-restored");
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(321L));
        var callback = Substitute.For<ISenderCallback>();
        var protocol = new AzureServiceBusSenderProtocol(runtime, endpoint, mapper, sender);
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = new Envelope(new ScheduledCancellationMessage()) { ScheduledTime = scheduledTime };
        var registration = new ScheduledMessageCancellationRegistration(envelope.Id);
        registry.Register(registration);
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        await protocol.SendBatchAsync(callback, batch);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        (await registration.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        await sender.Received(1).CancelScheduledMessageAsync(321L, CancellationToken.None);
    }

    [Fact]
    public async Task PartitionedBufferedSenderSchedulesCancellableEnvelopesNatively()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        var endpoint = new AzureServiceBusQueue(new AzureServiceBusTransport(), "scheduled-cancellation-partitioned");
        endpoint.Options.EnablePartitioning = true;
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(789L));
        var callback = Substitute.For<ISenderCallback>();
        var protocol = new AzureServiceBusSenderProtocol(runtime, endpoint, mapper, sender);
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = new Envelope(new ScheduledCancellationMessage()) { ScheduledTime = scheduledTime };
        var registration = new ScheduledMessageCancellationRegistration(envelope.Id);
        envelope.ScheduledCancellation = registration;
        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { envelope });

        await protocol.SendBatchAsync(callback, batch);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        var result = await registration.CancelAsync(CancellationToken.None);
        result.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        await sender.Received(1).CancelScheduledMessageAsync(789L, CancellationToken.None);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        await protocol.SendBatchAsync(callback, batch);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InlineSenderSchedulesCancellableEnvelopesNatively()
    {
        var endpoint = new AzureServiceBusQueue(new AzureServiceBusTransport(), "scheduled-cancellation-inline");
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(456L));
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var envelope = new Envelope(new ScheduledCancellationMessage()) { ScheduledTime = scheduledTime };
        var registration = new ScheduledMessageCancellationRegistration(envelope.Id);
        envelope.ScheduledCancellation = registration;
        var inlineSender = new InlineAzureServiceBusSender(endpoint, mapper, sender, NullLogger.Instance,
            CancellationToken.None);

        await inlineSender.SendAsync(envelope);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        await sender.DidNotReceive().SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());

        var result = await registration.CancelAsync(CancellationToken.None);
        result.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        await sender.Received(1).CancelScheduledMessageAsync(456L, CancellationToken.None);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        await inlineSender.SendAsync(envelope);

        await sender.DidNotReceive().SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InlineSenderSendsImmediatelyWhenCancellationScheduleIsAlreadyDue()
    {
        var endpoint = new AzureServiceBusQueue(new AzureServiceBusTransport(), "scheduled-cancellation-already-due");
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        DateTimeOffset? scheduledEnqueueTime = null;
        sender.SendMessageAsync(Arg.Do<ServiceBusMessage>(message => scheduledEnqueueTime = message.ScheduledEnqueueTime),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var envelope = new Envelope(new ScheduledCancellationMessage()) { ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var registration = new ScheduledMessageCancellationRegistration(envelope.Id);
        envelope.ScheduledCancellation = registration;
        var inlineSender = new InlineAzureServiceBusSender(endpoint, mapper, sender, NullLogger.Instance,
            CancellationToken.None);

        await inlineSender.SendAsync(envelope);

        scheduledEnqueueTime.ShouldBe(default(DateTimeOffset));
        (await registration.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotScheduled);
        await sender.DidNotReceive().ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InlineSenderFindsCancellationAfterEnvelopeRecoveryAndSkipsBoundSchedules()
    {
        var endpoint = new AzureServiceBusQueue(new AzureServiceBusTransport(), "scheduled-cancellation-recovered-inline");
        var mapper = Substitute.For<IOutgoingMapper<ServiceBusMessage>>();
        var sender = Substitute.For<ServiceBusSender>();
        sender.ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(987L));

        var registry = new ScheduledMessageCancellationRegistry();
        var envelopeId = Guid.NewGuid();
        var registration = new ScheduledMessageCancellationRegistration(envelopeId);
        registry.Register(registration);
        var inlineSender = new InlineAzureServiceBusSender(endpoint, mapper, sender, NullLogger.Instance,
            CancellationToken.None, registry);
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);

        // The recovered envelope has no in-memory registration; lookup by ID reconnects it to the live handle.
        var recovered = new Envelope(new ScheduledCancellationMessage())
        {
            Id = envelopeId,
            ScheduledTime = scheduledTime
        };
        await inlineSender.SendAsync(recovered);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        (await registration.CancelAsync(CancellationToken.None)).Status
            .ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
        await sender.Received(1).CancelScheduledMessageAsync(987L, CancellationToken.None);

        // A recovered duplicate for a schedule already accepted by Azure must not create another schedule.
        var duplicate = new Envelope(new ScheduledCancellationMessage())
        {
            Id = envelopeId,
            ScheduledTime = scheduledTime
        };
        await inlineSender.SendAsync(duplicate);

        await sender.Received(1).ScheduleMessageAsync(Arg.Any<ServiceBusMessage>(), scheduledTime,
            Arg.Any<CancellationToken>());
        await sender.DidNotReceive().SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
    }
}

public record ScheduledCancellationMessage;
