using System.Collections;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Wolverine.Transports.Tcp;
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
        var session = await Host.SendMessageAndWaitAsync(stickyMessage, timeoutInMilliseconds:5000);

        var records = session.Executed.MessagesOf<StickyMessageResponse>().ToArray();
        records.Length.ShouldBe(2);
        records.ShouldContain(new StickyMessageResponse("green", stickyMessage, new Uri("local://green")));
        records.ShouldContain(new StickyMessageResponse("blue", stickyMessage, new Uri("local://blue")));
    }

    [Fact]
    public async Task get_an_explanatory_message()
    {
        var stickyMessage = new StickyMessage();

        Func<IMessageContext, ValueTask> send = c => c.EndpointFor(new Uri("local://maroon")).SendAsync(stickyMessage);
        
        var session = await Host.TrackActivity().DoNotAssertOnExceptionsDetected().ExecuteAndWaitAsync(send);

        var ex = session.AllExceptions().OfType<NoHandlerForEndpointException>().FirstOrDefault();
        ex.ShouldNotBeNull();
        ex.MessageType.ShouldBe(typeof(StickyMessage));
        ex.Uri.ShouldBe(new Uri("local://maroon"));
    }

    public class FakeChainPolicy : IChainPolicy
    {
        public List<HandlerChain> Chains = new();

        public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
        {
            Chains.AddRange(chains.OfType<HandlerChain>());
        }
    }
    
    [Fact]
    public async Task handler_policies_apply_to_sticky_message_handlers()
    {
        var policy = new FakeChainPolicy();
        
        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(BlueStickyHandler))
                    .IncludeType(typeof(GreenStickyHandler));
                
                opts.Policies.Add(policy);

            }).StartAsync();
        
        // Original chain for StickyMessage
        // The sticky handler for "blue"
        // The sticky handler for "green"
        policy.Chains.Count.ShouldBe(3);

        foreach (var chain in policy.Chains)
        {
            // The DefaultApp sets this by policy, proving
            // that the log levels go to the sticky chains
            // to verify https://github.com/JasperFx/wolverine/issues/1054
            chain.ProcessingLogLevel.ShouldBe(LogLevel.Debug);
        }
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

    public static async Task explicit_listener()
    {
        #region sample_named_listener_endpoint

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // I'm explicitly configuring an incoming TCP
                // endpoint named "blue"
                opts.ListenAtPort(4000).Named("blue");
            }).StartAsync();

        #endregion
    }

    public static async Task explicit_listeners_by_fluent_interface()
    {
        #region sample_sticky_handlers_by_endpoint_with_fluent_interface

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(400)
                    // This handler type should be executed at this listening
                    // endpoint, but other handlers for the same message type
                    // should not
                    .AddStickyHandler(typeof(GreenStickyHandler));
                
                opts.ListenAtPort(5000)
                    // Likewise, the same StickyMessage received at this
                    // endpoint should be handled by BlueStickHandler
                    .AddStickyHandler(typeof(BlueStickyHandler));

            }).StartAsync();

        #endregion
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



#region sample_StickyMessage

public class StickyMessage;

    #endregion

    #region sample_using_sticky_handler_attribute

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

    #endregion



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

