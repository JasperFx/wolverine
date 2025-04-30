using System.Diagnostics;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling;

public class custom_action_for_inline_messages : IntegrationContext
{
    public custom_action_for_inline_messages(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task use_custom_action_inline_that_trips_off()
    {
        // Going to make it exhaust its retries so Wolverine
        // will have to get to the custom action
        InvoiceHandler.SucceedOnAttempt = 5;

        var session = await Host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .InvokeMessageAndWaitAsync(new ApproveInvoice("inv1"));
        
        session.MessageSucceeded.SingleMessage<RequireIntervention>()
            .InvoiceId
            .ShouldBe("inv1");
    }
    
    [Fact]
    public async Task use_custom_action_inline_that_does_not_get_reached()
    {
        // Going to make it exhaust its retries so Wolverine
        // will have to get to the custom action
        InvoiceHandler.SucceedOnAttempt = 0;

        var session = await Host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .InvokeMessageAndWaitAsync(new ApproveInvoice("inv1"));
        
        session.Executed.MessagesOf<RequireIntervention>()
            .Any().ShouldBeFalse();
    }
}

public record ApproveInvoice(string InvoiceId);
public record RequireIntervention(string InvoiceId);

public static class InvoiceHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException().RetryTimes(3)
            .Then
            .CompensatingAction<ApproveInvoice>((message, ex, bus) => bus.PublishAsync(new RequireIntervention(message.InvoiceId)), InvokeResult.Stop);
            
        // This is just a long hand way of doing the same thing as CompensatingAction
        // .CustomAction(async (runtime, lifecycle, _) =>
        // {
        //     if (lifecycle.Envelope.Message is ApproveInvoice message)
        //     {
        //         var bus = new MessageBus(runtime);
        //         await bus.PublishAsync(new RequireIntervention(message.InvoiceId));
        //     }
        //
        // }, "Send a compensating action", InvokeResult.Stop);
    }
    
    public static int SucceedOnAttempt = 0;
    
    public static void Handle(ApproveInvoice invoice, Envelope envelope)
    {
        if (envelope.Attempts >= SucceedOnAttempt) return;

        throw new Exception();
    }

    public static void Handle(RequireIntervention message)
    {
        Debug.WriteLine($"Got: {message}");
    }
}