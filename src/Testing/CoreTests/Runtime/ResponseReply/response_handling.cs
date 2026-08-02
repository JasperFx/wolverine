using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace CoreTests.Runtime.ResponseReply;

public class response_handling : IDisposable
{
    private readonly ReplyTracker _theListener = new(NullLogger<ReplyTracker>.Instance, 5);

    public void Dispose()
    {
        _theListener?.Dispose();
    }

    [Fact]
    public void register_and_unregister()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = _theListener.RegisterListener<Message1>(envelope, CancellationToken.None, 5.Seconds());

        _theListener.HasListener(envelope.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task wait_successfully()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = _theListener.RegisterListener<Message1>(envelope, CancellationToken.None, 5.Seconds());

        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

        var theMessage = new Message1();
        var response = new Envelope
        {
            ConversationId = envelope.Id,
            Message = theMessage
        };

        _theListener.Complete(response);


        (await waiter).ShouldBe(theMessage);

        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task wait_failure()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = _theListener.RegisterListener<Message1>(envelope, CancellationToken.None, 5.Seconds());

        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

        var ack = new FailureAcknowledgement { Message = "Bad!" };
        var response = new Envelope
        {
            ConversationId = envelope.Id,
            Message = ack
        };

        _theListener.Complete(response);

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Should.ThrowAsync<WolverineRequestReplyException>(() => waiter);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task timeout_failure()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = _theListener.RegisterListener<Message1>(envelope, CancellationToken.None, 250.Milliseconds());

        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Should.ThrowAsync<TimeoutException>(() => waiter);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks


        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }

    /// <summary>
    /// GH-3781. A caller handing in a token that is ALREADY cancelled has to fail immediately, not sit out
    /// the reply window. CancellationTokenRegistration runs its callback synchronously for a cancelled
    /// token, and the listener used to register the caller's token before assigning _completion -- so the
    /// callback's `_completion?.TrySetException(...)` fired against null and did nothing at all. Every
    /// shutdown path passes a cancelled token, and for a batched agent command the window it then waited
    /// out is AgentBatchTimeouts.ReplyWindowFor(chunk): 25.5 minutes at the shipped AgentStartBatchSize of
    /// 50, paid inside IHost.StopAsync.
    /// </summary>
    [Fact]
    public async Task a_token_that_is_already_cancelled_fails_the_listener_immediately()
    {
        var envelope = ObjectMother.Envelope();

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // A window long enough that "waited it out" and "failed fast" cannot be confused.
        var waiter = _theListener.RegisterListener<Message1>(envelope, cancellation.Token, 30.Seconds());

        waiter.IsCompleted.ShouldBeTrue();
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Should.ThrowAsync<TimeoutException>(() => waiter);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

        // ...and it must not be left in the dictionary: it completed before it was ever registered, so
        // nothing would take it out again.
        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task a_token_cancelled_after_registration_fails_the_listener()
    {
        var envelope = ObjectMother.Envelope();

        using var cancellation = new CancellationTokenSource();

        var waiter = _theListener.RegisterListener<Message1>(envelope, cancellation.Token, 30.Seconds());
        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

        await cancellation.CancelAsync();

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Should.ThrowAsync<TimeoutException>(() => waiter);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }
}