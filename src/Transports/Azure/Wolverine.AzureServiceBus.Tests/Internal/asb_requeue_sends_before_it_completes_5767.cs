using Azure.Messaging.ServiceBus;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// GH-5767. Azure Service Bus requeues (and reschedules) by sending a COPY back to the entity and settling the
/// original. Every listener used to complete the original first: a send that failed, or a process that exited in
/// between, lost the message for good. A NativeAck endpoint takes this path for every envelope still in its lanes
/// on a graceful shutdown.
///
/// <para>The copy also has to survive the broker's duplicate detection, which keys on <c>MessageId</c>, and has
/// to keep the session it was received in when an interop mapper does not carry <c>SessionId</c> through
/// <see cref="Envelope.GroupId" />.</para>
///
/// <para>No broker: the requeue sender is the real <see cref="InlineAzureServiceBusSender" /> over a substituted
/// <see cref="ServiceBusSender" />, and the original settles through a substituted receiver.</para>
/// </summary>
public class asb_requeue_sends_before_it_completes_5767 : IAsyncDisposable
{
    private readonly ServiceBusSender theServiceBusSender = Substitute.For<ServiceBusSender>();
    private readonly ServiceBusReceiver theOriginalsReceiver = Substitute.For<ServiceBusReceiver>();
    private readonly List<ServiceBusMessage> theCopies = new();
    private readonly AzureServiceBusTransport theTransport = new();
    private readonly AzureServiceBusQueue theQueue;
    private IListener? theListener;

    public asb_requeue_sends_before_it_completes_5767()
    {
        theQueue = theTransport.Queues["gh5767"];
    }

    public static IEnumerable<object[]> Listeners() =>
    [
        ["inline"], ["batched"], ["inline-session"], ["session-specific"]
    ];

    private ISender requeueSender(IAzureServiceBusEnvelopeMapper? mapper = null)
    {
        mapper ??= theQueue.BuildMapper(Substitute.For<IWolverineRuntime>());
        return new InlineAzureServiceBusSender(theQueue, mapper, theServiceBusSender, NullLogger.Instance,
            CancellationToken.None);
    }

    private IListener buildListener(string kind, ISender requeue)
    {
        // Never connected: none of these listeners is started, and the requeue path only touches the sender
        // and the receiver the ENVELOPE carries
        var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);
        var mapper = Substitute.For<IAzureServiceBusEnvelopeMapper>();

        theListener = kind switch
        {
            "inline" => new InlineAzureServiceBusListener(theQueue, NullLogger.Instance,
                client.CreateProcessor("never-used"), Substitute.For<IReceiver>(), mapper, requeue, 3),
            "batched" => new BatchedAzureServiceBusListener(theQueue, NullLogger.Instance,
                Substitute.For<IReceiver>(), Substitute.For<ServiceBusReceiver>(), mapper, requeue, 3),
            "inline-session" => new InlineAzureServiceBusSessionListener(theQueue, NullLogger.Instance,
                client.CreateSessionProcessor("never-used"), Substitute.For<IReceiver>(), mapper, requeue),
            "session-specific" => new SessionSpecificListener(Substitute.For<ServiceBusSessionReceiver>(), theQueue,
                Substitute.For<IReceiver>(), mapper, NullLogger.Instance, requeue),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        return theListener;
    }

    private AzureServiceBusEnvelope receivedEnvelope(string? sessionId = null)
    {
        var id = Guid.NewGuid();
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: id.ToString(),
            sessionId: sessionId, lockTokenGuid: Guid.NewGuid());

        return new AzureServiceBusEnvelope(message, theOriginalsReceiver)
        {
            Id = id,
            Data = "{}"u8.ToArray(),
            MessageType = "ping",
            ContentType = "application/json"
        };
    }

    private void sendsSucceed()
    {
        theServiceBusSender.SendMessageAsync(Arg.Do<ServiceBusMessage>(theCopies.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private int completions() => theOriginalsReceiver.ReceivedCalls()
        .Count(x => x.GetMethodInfo().Name == nameof(ServiceBusReceiver.CompleteMessageAsync));

    private static async Task eventually(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(50.Milliseconds());
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (theListener != null)
        {
            await theListener.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Listeners))]
    public async Task a_requeue_whose_send_fails_leaves_the_original_unsettled(string kind)
    {
        theServiceBusSender.SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceBusException("the link is closed", ServiceBusFailureReason.ServiceCommunicationProblem));

        var listener = buildListener(kind, requeueSender());
        var envelope = receivedEnvelope();

        // The first attempt runs inline in PostAsync, so it has failed by the time DeferAsync returns
        await listener.DeferAsync(envelope);

        completions().ShouldBe(0,
            "The copy was never accepted, so the original is the only copy there is. Its lock lapses and Azure Service Bus redelivers it.");
        envelope.IsCompleted.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Listeners))]
    public async Task a_requeue_sends_the_copy_before_it_completes_the_original(string kind)
    {
        sendsSucceed();

        var listener = buildListener(kind, requeueSender());
        var envelope = receivedEnvelope();

        await listener.DeferAsync(envelope);

        Received.InOrder(() =>
        {
            _ = theServiceBusSender.SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
            _ = theOriginalsReceiver.CompleteMessageAsync(envelope.AzureMessage, Arg.Any<CancellationToken>());
        });

        envelope.IsCompleted.ShouldBeTrue();

        // GH-3710: a requeue is not a terminal
        envelope.HasBeenAcked.ShouldBeFalse();
    }

    [Fact]
    public async Task a_scheduled_retry_sends_the_copy_before_it_completes_the_original()
    {
        sendsSucceed();

        var listener = (ISupportNativeScheduling)buildListener("inline", requeueSender());
        var envelope = receivedEnvelope();

        await listener.MoveToScheduledUntilAsync(envelope, DateTimeOffset.UtcNow.AddMinutes(1));

        Received.InOrder(() =>
        {
            _ = theServiceBusSender.SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
            _ = theOriginalsReceiver.CompleteMessageAsync(envelope.AzureMessage, Arg.Any<CancellationToken>());
        });

        theCopies.ShouldHaveSingleItem().ScheduledEnqueueTime.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task a_retry_after_a_failed_complete_does_not_send_a_second_copy()
    {
        sendsSucceed();

        var attempts = 0;
        theOriginalsReceiver.CompleteMessageAsync(Arg.Any<ServiceBusReceivedMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy))
                : Task.CompletedTask);

        var listener = buildListener("batched", requeueSender());
        var envelope = receivedEnvelope();

        await listener.DeferAsync(envelope);

        await eventually(() => envelope.IsCompleted, "The retry never completed the original");

        completions().ShouldBe(2);
        theCopies.Count.ShouldBe(1, "The first attempt's copy was accepted; the retry only has the complete left to do");
    }

    [Fact]
    public async Task the_copy_gets_its_own_message_id_and_keeps_the_envelope_id()
    {
        sendsSucceed();

        var listener = buildListener("inline", requeueSender());
        var envelope = receivedEnvelope();

        await listener.DeferAsync(envelope);

        var copy = theCopies.ShouldHaveSingleItem();
        copy.MessageId.ShouldNotBeNullOrEmpty();
        copy.MessageId.ShouldNotBe(envelope.AzureMessage.MessageId,
            "On an entity with duplicate detection, Azure Service Bus accepts a send that reuses a MessageId inside the window and silently drops it");

        // What the receiving node makes of the copy: the same Wolverine envelope
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(body: copy.Body, messageId: copy.MessageId,
            properties: copy.ApplicationProperties, subject: copy.Subject, contentType: copy.ContentType);
        var mapped = new Envelope();
        theQueue.BuildMapper(Substitute.For<IWolverineRuntime>()).MapIncomingToEnvelope(mapped, received);

        mapped.Id.ShouldBe(envelope.Id);
    }

    [Fact]
    public async Task every_retry_of_one_requeue_sends_the_same_message_id()
    {
        var attempts = 0;
        theServiceBusSender.SendMessageAsync(Arg.Do<ServiceBusMessage>(theCopies.Add), Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts == 1
                ? Task.FromException(new ServiceBusException("timed out", ServiceBusFailureReason.ServiceTimeout))
                : Task.CompletedTask);

        var listener = buildListener("batched", requeueSender());
        var envelope = receivedEnvelope();

        await listener.DeferAsync(envelope);

        await eventually(() => envelope.IsCompleted, "The retry never completed the original");

        // An ambiguous first send (it timed out, but the broker may have taken it) is deduplicated against the
        // retry rather than producing a second copy
        theCopies.Count.ShouldBe(2);
        theCopies.Select(x => x.MessageId).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task the_copy_keeps_the_received_session_when_the_mapper_does_not_carry_it()
    {
        sendsSucceed();

        // An interop mapper that knows nothing of sessions: Envelope.GroupId stays empty
        var listener = buildListener("session-specific", requeueSender(Substitute.For<IAzureServiceBusEnvelopeMapper>()));
        var envelope = receivedEnvelope(sessionId: "session-1");

        await listener.DeferAsync(envelope);

        theCopies.ShouldHaveSingleItem().SessionId.ShouldBe("session-1",
            "A session-enabled entity rejects a message without a SessionId");
    }
}
