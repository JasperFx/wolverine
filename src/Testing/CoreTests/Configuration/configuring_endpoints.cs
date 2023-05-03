using System;
using System.Linq;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

public class configuring_endpoints : IDisposable
{
    private readonly IHost _host;
    private readonly IWolverineRuntime theRuntime;
    private readonly WolverineOptions theOptions;

    public configuring_endpoints()
    {
        _host = Host.CreateDefaultBuilder().UseWolverine(x =>
        {
            x.ListenForMessagesFrom("local://one").Sequential().Named("one");
            x.ListenForMessagesFrom("local://two").MaximumParallelMessages(11);
            x.ListenForMessagesFrom("local://three").UseDurableInbox();
            x.ListenForMessagesFrom("local://four").UseDurableInbox().BufferedInMemory();
            x.ListenForMessagesFrom("local://five").ProcessInline();

            x.ListenForMessagesFrom("local://durable1").UseDurableInbox(new BufferingLimits(500, 250));
            x.ListenForMessagesFrom("local://buffered1").BufferedInMemory(new BufferingLimits(250, 100));
        }).Build();

        theOptions = _host.Get<WolverineOptions>();
        theRuntime = _host.Get<IWolverineRuntime>();
    }

    private StubTransport theStubTransport
    {
        get
        {
            var transport = theOptions.Transports.GetOrCreate<StubTransport>();
            foreach (var endpoint in transport.Endpoints) endpoint.Compile(theRuntime);

            return transport;
        }
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    private LocalQueue localQueue(string queueName)
    {
        var settings = theOptions.Transports.GetOrCreate<LocalTransport>()
            .QueueFor(queueName);

        settings.Compile(theRuntime);

        return settings;
    }

    private Endpoint findEndpoint(string uri)
    {
        var endpoint = theOptions.Transports
            .TryGetEndpoint(uri.ToUri());

        endpoint.Compile(theRuntime);

        return endpoint;
    }

    [Fact]
    public void has_default_buffering_options_on_buffered()
    {
        var queue = localQueue("four");
        queue.BufferingLimits.Maximum.ShouldBe(1000);
        queue.BufferingLimits.Restart.ShouldBe(500);
    }

    [Fact]
    public void override_buffering_limits_on_buffered()
    {
        var queue = localQueue("buffered1");
        queue.BufferingLimits.Maximum.ShouldBe(250);
        queue.BufferingLimits.Restart.ShouldBe(100);
    }

    [Fact]
    public void override_buffering_limits_on_durable()
    {
        var queue = localQueue("durable1");
        queue.BufferingLimits.Maximum.ShouldBe(500);
        queue.BufferingLimits.Restart.ShouldBe(250);
    }

    [Fact]
    public void can_set_the_endpoint_name()
    {
        localQueue("one").EndpointName.ShouldBe("one");
    }

    [Fact]
    public void publish_all_adds_an_all_subscription_to_the_endpoint()
    {
        theOptions.PublishAllMessages()
            .To("stub://5555");

        findEndpoint("stub://5555")
            .Subscriptions.Single()
            .Scope.ShouldBe(RoutingScope.All);
    }

    [Fact]
    public void configure_default_queue()
    {
        theOptions.DefaultLocalQueue
            .MaximumParallelMessages(13);

        localQueue(TransportConstants.Default)
            .ExecutionOptions.MaxDegreeOfParallelism
            .ShouldBe(13);
    }

    [Fact]
    public void configure_durable_queue()
    {
        theOptions.DurableScheduledMessagesLocalQueue
            .MaximumParallelMessages(22);

        localQueue(TransportConstants.Durable)
            .ExecutionOptions.MaxDegreeOfParallelism
            .ShouldBe(22);
    }

    [Fact]
    public void mark_durable()
    {
        theOptions.ListenForMessagesFrom("stub://1111")
            .UseDurableInbox();

        var endpoint = findEndpoint("stub://1111");
        endpoint.ShouldNotBeNull();
        endpoint.Mode.ShouldBe(EndpointMode.Durable);
        endpoint.IsListener.ShouldBeTrue();
    }

    [Fact]
    public void prefer_listener()
    {
        theOptions.ListenForMessagesFrom("stub://1111");
        theOptions.ListenForMessagesFrom("stub://2222");
        theOptions.ListenForMessagesFrom("stub://3333").UseForReplies();

        findEndpoint("stub://1111").IsUsedForReplies.ShouldBeFalse();
        findEndpoint("stub://2222").IsUsedForReplies.ShouldBeFalse();
        findEndpoint("stub://3333").IsUsedForReplies.ShouldBeTrue();
    }

    [Fact]
    public void configure_sequential()
    {
        localQueue("one")
            .ExecutionOptions
            .MaxDegreeOfParallelism
            .ShouldBe(1);
    }

    [Fact]
    public void configure_max_parallelization()
    {
        localQueue("two")
            .ExecutionOptions
            .MaxDegreeOfParallelism
            .ShouldBe(11);
    }

    [Fact]
    public void configure_process_inline()
    {
        theOptions
            .ListenForMessagesFrom("local://three")
            .ProcessInline();


        localQueue("three")
            .Mode
            .ShouldBe(EndpointMode.Inline);
    }

    [Fact]
    public void configure_durable()
    {
        theOptions
            .ListenForMessagesFrom("local://three")
            .UseDurableInbox();


        localQueue("three")
            .Mode
            .ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public void configure_not_durable()
    {
        theOptions.ListenForMessagesFrom("local://four");

        localQueue("four")
            .Mode
            .ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public void configure_execution()
    {
        theOptions.LocalQueue("foo")
            .ConfigureExecution(x => x.BoundedCapacity = 111);

        localQueue("foo")
            .ExecutionOptions.BoundedCapacity.ShouldBe(111);
    }

    [Fact]
    public void sets_is_listener()
    {
        var uriString = "stub://1111";
        theOptions.ListenForMessagesFrom(uriString);

        findEndpoint(uriString)
            .IsListener.ShouldBeTrue();
    }


    [Fact]
    public void select_reply_endpoint_with_one_listener()
    {
        theOptions.ListenForMessagesFrom("stub://2222");
        theOptions.PublishAllMessages().To("stub://3333");

        theStubTransport.ReplyEndpoint()
            .Uri.ShouldBe("stub://2222".ToUri());
    }

    [Fact]
    public void select_reply_endpoint_with_multiple_listeners_and_one_designated_reply_endpoint()
    {
        theOptions.ListenForMessagesFrom("stub://2222");
        theOptions.ListenForMessagesFrom("stub://4444").UseForReplies();
        theOptions.ListenForMessagesFrom("stub://5555");
        theOptions.PublishAllMessages().To("stub://3333");

        theStubTransport.ReplyEndpoint()
            .Uri.ShouldBe("stub://4444".ToUri());
    }

    [Fact]
    public void select_reply_endpoint_with_no_listeners()
    {
        theOptions.PublishAllMessages().To("stub://3333");
        theStubTransport.ReplyEndpoint().ShouldBeNull();
    }
}