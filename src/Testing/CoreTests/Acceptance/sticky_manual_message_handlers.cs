using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// One message type, two pre-built handlers: one registered globally with AddMessageHandler, the other only on
/// local queue "sticky-a". Sticky handlers used to work only for discovered handler methods, and
/// AddMessageHandler keeps one handler per message type, so this was impossible. CritterWatch's embedded console
/// needs it for a message that the console relays and the monitored application handles on its own queue.
/// </summary>
public class sticky_manual_message_handlers : IAsyncLifetime
{
    private readonly ManualStickyRecordingHandler theGlobalHandler = new("global");
    private readonly ManualStickyRecordingHandler theStickyHandler = new("sticky");
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                opts.AddMessageHandler(theGlobalHandler);

                #region sample_registering_a_sticky_manual_message_handler
                opts.LocalQueue("sticky-a").AddStickyHandler(typeof(StickyPing), theStickyHandler);
                #endregion

                opts.LocalQueue("sticky-b");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_message_received_on_the_sticky_queue_runs_the_sticky_handler()
    {
        await _host.TrackActivity().ExecuteAndWaitAsync(c => c.EndpointFor("sticky-a").SendAsync(new StickyPing("a")));

        theStickyHandler.Received.ShouldBe(["a"]);
        theGlobalHandler.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_message_received_on_another_queue_runs_the_global_handler()
    {
        await _host.TrackActivity().ExecuteAndWaitAsync(c => c.EndpointFor("sticky-b").SendAsync(new StickyPing("b")));

        theGlobalHandler.Received.ShouldBe(["b"]);
        theStickyHandler.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task invoke_runs_the_global_handler()
    {
        await _host.InvokeMessageAndWaitAsync(new StickyPing("invoked"));

        theGlobalHandler.Received.ShouldBe(["invoked"]);
        theStickyHandler.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_sticky_handler_fails_by_its_own_chain()
    {
        // ConfigureChain discards a DivideByZeroException for the sticky handler only. The global handler's
        // chain has no such rule, so the same failure there goes to the dead letter queue.
        theStickyHandler.Chain.ShouldNotBeNull();
        theStickyHandler.Chain.ShouldNotBeSameAs(theGlobalHandler.Chain);

        var sticky = await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.EndpointFor("sticky-a").SendAsync(new StickyPing("fail")));
        sticky.Discarded.SingleMessage<StickyPing>().Name.ShouldBe("fail");

        var global = await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.EndpointFor("sticky-b").SendAsync(new StickyPing("fail")));
        global.MovedToErrorQueue.SingleMessage<StickyPing>().Name.ShouldBe("fail");
    }

    [Fact]
    public void the_global_lookup_never_sees_the_sticky_handler()
    {
        _host.GetRuntime().Options.HandlerGraph.HandlerFor(typeof(StickyPing)).ShouldBeSameAs(theGlobalHandler);
    }
}

public record StickyPing(string Name);

public class ManualStickyRecordingHandler(string name) : MessageHandler<StickyPing>
{
    public List<string> Received { get; } = [];

    public override void ConfigureChain(HandlerChain chain)
    {
        if (name == "sticky")
        {
            chain.OnException<DivideByZeroException>().Discard();
        }
    }

    protected override Task HandleAsync(StickyPing message, MessageContext context, CancellationToken cancellation)
    {
        if (message.Name == "fail") throw new DivideByZeroException();

        Received.Add(message.Name);
        return Task.CompletedTask;
    }
}
