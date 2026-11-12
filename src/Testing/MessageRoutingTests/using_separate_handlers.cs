using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace MessageRoutingTests;

public class using_separate_handlers : MessageRoutingContext
{
    private static async Task MultipleHandlerBehaviorUsage()
    {
        #region sample_using_multiplehandlerbehavior
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // GH-5139. Nothing to configure: Wolverine 7 always gives every handler type of a
                // message type its own handler chain and its own local queue
            }).StartAsync();

        #endregion
    }
    
    protected override void configure(WolverineOptions opts)
    {
    }

    [Fact]
    public void automatically_make_each_handler_be_sticky()
    {
        assertRoutesAre<ColorMessage>("local://red", "local://blue", "local://messageroutingtests.greencolormessagehandler");
    }

    [Fact]
    public async Task sets_up_sticky_routing_automatically_for_multiple_handlers()
    {
        RedColorMessageHandler.LastMessage = null;
        GreenColorMessageHandler.LastMessage = null;
        BlueColorMessageHandler.LastMessage = null;
        
        var red = new ColorMessage();
        await theHost.ExecuteAndWaitAsync(async c => await c.EndpointFor("red").SendAsync(red));
        RedColorMessageHandler.LastMessage.ShouldBe(red);
        GreenColorMessageHandler.LastMessage.ShouldBeNull();

        var green = new ColorMessage();
        await theHost.ExecuteAndWaitAsync(async c => await c.EndpointFor("messageroutingtests.greencolormessagehandler").SendAsync(green));
        RedColorMessageHandler.LastMessage.ShouldBe(red);
        GreenColorMessageHandler.LastMessage.ShouldBe(green);
        
        var blue = new ColorMessage();
        await theHost.ExecuteAndWaitAsync(async c => await c.EndpointFor("blue").SendAsync(blue));
        
        BlueColorMessageHandler.LastMessage.ShouldBe(blue);
    }

    [Fact]
    public async Task publish_message_should_go_independently_to_all_three()
    {
        var tracked = await theHost.SendMessageAndWaitAsync(new ColorMessage());
        
        tracked.Executed.Envelopes().Select(x => x.Destination!).OrderBy(x => x.ToString())
            .ShouldBe([new Uri("local://blue"), new Uri("local://messageroutingtests.greencolormessagehandler"), new Uri("local://red")]);
    }

}