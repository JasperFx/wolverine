using System.Collections;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Acceptance;

public class sticky_message_handlers : IntegrationContext
{
    public sticky_message_handlers(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task send_message_is_handled_by_both_handlers_independently_by_attributes()
    {
        var stickyMessage = new StickyMessage();
        var session = await Host.SendMessageAndWaitAsync(stickyMessage, timeoutInMilliseconds:60000);

        var records = session.Executed.MessagesOf<StickyMessageResponse>().ToArray();
        records.Length.ShouldBe(2);
        records.ShouldContain(new StickyMessageResponse("green", stickyMessage, new Uri("local://green")));
        records.ShouldContain(new StickyMessageResponse("blue", stickyMessage, new Uri("local://blue")));
    }
}

public class when_definining_sticky_handlers_by_fluent_interface
{
    [Fact]
    public async Task message_should_be_handled_separately_on_different_local_queues()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(BlueSticky2Handler));
                opts.Discovery.IncludeType(typeof(GreenSticky2Handler));
                opts.Discovery.IncludeType<StickyMessageResponseHandler>();

                opts.LocalQueue("blue").AddStickyHandler(typeof(BlueSticky2Handler));
                opts.LocalQueue("green").AddStickyHandler(typeof(GreenSticky2Handler));
            }).StartAsync();
        
        var stickyMessage = new StickyMessage2();
        var session = await host.SendMessageAndWaitAsync(stickyMessage, timeoutInMilliseconds:60000);

        var records = session.Executed.MessagesOf<StickyMessageResponse>().ToArray();
        records.Length.ShouldBe(2);
        records.ShouldContain(new StickyMessageResponse("green", stickyMessage, new Uri("local://green")));
        records.ShouldContain(new StickyMessageResponse("blue", stickyMessage, new Uri("local://blue")));
    }
}

public class when_building_a_handler_chain_for_sticky_handlers
{
    private WolverineOptions theOptions = new();
    private readonly HandlerChain theChain;

    public when_building_a_handler_chain_for_sticky_handlers()
    {
        theOptions.Transports.ForScheme("stub").TryGetEndpoint("stub://green".ToUri())
            .EndpointName = "green";
        
        var blue = new HandlerCall(typeof(BlueStickyHandler), nameof(BlueStickyHandler.Handle));
        var green = new HandlerCall(typeof(GreenStickyHandler), nameof(GreenStickyHandler.Handle));
        var grouping = new HandlerGrouping(typeof(StickyMessage), [blue, green]);
        
        theChain = new HandlerChain(theOptions, grouping, theOptions.HandlerGraph);
    }

    [Fact]
    public void should_split_into_separate_chains()
    {
        theChain.ByEndpoint.Count.ShouldBe(2);

        foreach (var chain in theChain.ByEndpoint)
        {
            chain.Handlers.Count.ShouldBe(1);
        }
        
        theChain.Handlers.Count.ShouldBe(0);
    }

    [Fact]
    public void use_a_local_queue_when_there_is_no_named_endpoint_for_the_attribute()
    {
        var blue = theChain.ByEndpoint.Single(x => x.Handlers.Single().HandlerType == typeof(BlueStickyHandler));
        blue.Endpoints.Single().ShouldBeOfType<LocalQueue>()
            .EndpointName.ShouldBe("blue");
    }

    [Fact]
    public void use_a_named_endpoint_if_one_already_exists()
    {
        var blue = theChain.ByEndpoint.Single(x => x.Handlers.Single().HandlerType == typeof(GreenStickyHandler));
        blue.Endpoints.Single().ShouldBeOfType<StubEndpoint>()
            .EndpointName.ShouldBe("green");
    }

    
}

public class HandlerGrouping : IGrouping<Type, HandlerCall>
{
    private readonly HandlerCall[] _calls;

    public HandlerGrouping(Type messageType, HandlerCall[] calls)
    {
        Key = messageType;
        _calls = calls;
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IEnumerator<HandlerCall> GetEnumerator()
    {
        return _calls.As<IEnumerable<HandlerCall>>().GetEnumerator();
    }

    public Type Key { get; set; }
}



public class StickyMessage{}

[StickyHandler("blue")]
public static class BlueStickyHandler
{
    public static StickyMessageResponse Handle(StickyMessage message, Envelope envelope)
    {
        return new StickyMessageResponse("blue", message, envelope.Destination);
    }
}

[StickyHandler("green")]
public static class GreenStickyHandler
{
    public static StickyMessageResponse Handle(StickyMessage message, Envelope envelope)
    {
        return new StickyMessageResponse("green", message, envelope.Destination);
    }
}

public record StickyMessageResponse(string Color, StickyMessage Message, Uri Destination)
{
    public override string ToString()
    {
        return $"{nameof(Color)}: {Color}, {nameof(Message)}: {Message}, {nameof(Destination)}: {Destination}";
    }
}

public class StickyMessageResponseHandler
{
    public static void Handle(StickyMessageResponse response)
    {
        // nothing
    }
}


public class StickyMessage2 : StickyMessage{}


public static class BlueSticky2Handler
{
    public static StickyMessageResponse Handle(StickyMessage2 message, Envelope envelope)
    {
        return new StickyMessageResponse("blue", message, envelope.Destination);
    }
}

public static class GreenSticky2Handler
{
    public static StickyMessageResponse Handle(StickyMessage2 message, Envelope envelope)
    {
        return new StickyMessageResponse("green", message, envelope.Destination);
    }
}

