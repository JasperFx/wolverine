using System;
using System.Threading;
using System.Threading.Tasks;
using CoreTests.Messaging;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using TestingSupport.Compliance;
using Wolverine.Runtime;
using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace CoreTests.Runtime.ResponseReply;

public class response_handling : IDisposable
{
    private readonly ReplyTracker _theListener = new(NullLogger<ReplyTracker>.Instance);

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

        await Should.ThrowAsync<WolverineRequestReplyException>(async () => { await waiter; });

        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }


    [Fact]
    public async Task timeout_failure()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = _theListener.RegisterListener<Message1>(envelope, CancellationToken.None, 250.Milliseconds());

        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

        await Should.ThrowAsync<TimeoutException>(async () => { await waiter; });

        _theListener.HasListener(envelope.Id).ShouldBeFalse();
    }
}