using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.ErrorHandling;

public class custom_error_action_raises_new_message_1 : IAsyncLifetime
{
    private IHost theReceiver;
    private IHost theSender;

    public async Task InitializeAsync()
    {
        var senderPort = PortFinder.GetAvailablePort();
        var receiverPort = PortFinder.GetAvailablePort();

        theSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToPort(receiverPort);
                opts.ListenAtPort(senderPort);
                opts.ServiceName = "Sender";
            }).StartAsync();

        #region sample_inline_exception_handling_action

        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(receiverPort);
                opts.ServiceName = "Receiver";

                opts.Handlers.OnException<ShippingFailedException>()
                    .Discard().And(async (_, context, _) =>
                    {
                        if (context.Envelope?.Message is ShipOrder cmd)
                        {
                            await context.RespondToSenderAsync(new ShippingFailed(cmd.OrderId));
                        }
                    });
            }).StartAsync();

        #endregion
    }

    public async Task DisposeAsync()
    {
        await theReceiver.StopAsync();
        await theSender.StopAsync();
    }

    [Fact]
    public async Task send_message_and_get_response_on_failure()
    {
        var session = await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeReceivedAt<ShippingFailed>(theSender)
            .SendMessageAndWaitAsync(new ShipOrder(5));

        var env = session.Received.SingleEnvelope<ShippingFailed>();
        env.Source.ShouldBe("Receiver");
        env.Message.ShouldBeOfType<ShippingFailed>()
            .OrderId.ShouldBe(5);
    }
}

#region sample_ShippingOrderFailurePolicy

public class ShippingOrderFailurePolicy : UserDefinedContinuation
{
    public ShippingOrderFailurePolicy() : base(
        $"Send a {nameof(ShippingFailed)} back to the sender on shipping order failures")
    {
    }

    public override async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now)
    {
        if (lifecycle.Envelope?.Message is ShipOrder cmd)
        {
            await lifecycle
                .RespondToSenderAsync(new ShippingFailed(cmd.OrderId));
        }
    }
}

#endregion

public class custom_error_action_raises_new_message_2 : IAsyncLifetime
{
    private IHost theReceiver;
    private IHost theSender;

    public async Task InitializeAsync()
    {
        var senderPort = PortFinder.GetAvailablePort();
        var receiverPort = PortFinder.GetAvailablePort();

        theSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages().ToPort(receiverPort);
                opts.ListenAtPort(senderPort);
                opts.ServiceName = "Sender";
            }).StartAsync();

        #region sample_registering_custom_user_continuation_policy

        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(receiverPort);
                opts.ServiceName = "Receiver";

                opts.Handlers.OnException<ShippingFailedException>()
                    .Discard().And<ShippingOrderFailurePolicy>();
            }).StartAsync();

        #endregion
    }

    public async Task DisposeAsync()
    {
        await theReceiver.StopAsync();
        await theSender.StopAsync();
    }

    [Fact]
    public async Task send_message_and_get_response_on_failure()
    {
        var session = await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeReceivedAt<ShippingFailed>(theSender)
            .SendMessageAndWaitAsync(new ShipOrder(5));

        var env = session.Received.SingleEnvelope<ShippingFailed>();
        env.Source.ShouldBe("Receiver");
        env.Message.ShouldBeOfType<ShippingFailed>()
            .OrderId.ShouldBe(5);
    }
}

public record ShipOrder(int OrderId);

public class ShipOrderHandler
{
    public void Handle(ShipOrder command)
    {
        // blows up!
        throw new ShippingFailedException();
    }
}

public class ShippingFailedException : Exception
{
}

public record ShippingFailed(int OrderId);

public class ShippingFailedHandler
{
    public void Handle(ShippingFailed failed)
    {
        // Maybe send an email here?
        // Send a text?
        // Alert users somehow?
    }
}