using System;
using System.Threading.Tasks;
using Baseline.Dates;
using CoreTests.Messaging;
using TestMessages;
using Wolverine.Runtime.ResponseReply;
using Xunit;

namespace CoreTests.Runtime.ResponseReply;

public class response_handling : IDisposable
{
    private readonly ResponseHandler theHandler = new ResponseHandler();

    public void Dispose()
    {
        theHandler?.Dispose();
    }

    [Fact]
    public void register_and_unregister()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = theHandler.RegisterCallback<Message1>(envelope, 5.Seconds());
        
        theHandler.HasListener(envelope.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task wait_successfully()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = theHandler.RegisterCallback<Message1>(envelope, 5.Seconds());
        
        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

        var theMessage = new Message1();
        var response = new Envelope
        {
            ConversationId = envelope.Id,
            Message = theMessage
        };

        theHandler.Complete(response);


        (await waiter).ShouldBe(theMessage);
        
        theHandler.HasListener(envelope.Id).ShouldBeFalse();
    }
    
    [Fact]
    public async Task wait_failure()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = theHandler.RegisterCallback<Message1>(envelope, 5.Seconds());
        
        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);

        var ack = new FailureAcknowledgement{Message = "Bad!"};
        var response = new Envelope
        {
            ConversationId = envelope.Id,
            Message = ack
        };

        theHandler.Complete(response);

        await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
        {
            await waiter;
        });
        
        theHandler.HasListener(envelope.Id).ShouldBeFalse();
    }
    
        
    [Fact]
    public async Task timeout_failure()
    {
        var envelope = ObjectMother.Envelope();

        var waiter = theHandler.RegisterCallback<Message1>(envelope, 250.Milliseconds());
        
        waiter.Status.ShouldBe(TaskStatus.WaitingForActivation);
        
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await waiter;
        });
        
        theHandler.HasListener(envelope.Id).ShouldBeFalse();
    }
}