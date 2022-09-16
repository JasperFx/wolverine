using System;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
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
    private WolverineOptions theOptions;

    public configuring_endpoints()
    {
        _host = Host.CreateDefaultBuilder().UseWolverine(x =>
        {
            x.ListenForMessagesFrom("local://one").Sequential().Named("one");
            x.ListenForMessagesFrom("local://two").MaximumParallelMessages(11);
            x.ListenForMessagesFrom("local://three").UseDurableInbox();
            x.ListenForMessagesFrom("local://four").UseDurableInbox().BufferedInMemory();
            x.ListenForMessagesFrom("local://five").ProcessInline();
        }).Build();

        theOptions = _host.Get<WolverineOptions>();
    }

    private StubTransport theStubTransport => theOptions.GetOrCreate<StubTransport>();

    public void Dispose()
    {
        _host.Dispose();
    }

    private LocalQueueSettings localQueue(string queueName)
    {
        return theOptions.GetOrCreate<LocalTransport>()
            .QueueFor(queueName);
    }

    private Endpoint findEndpoint(string uri)
    {
        return theOptions
            .TryGetEndpoint(uri.ToUri());
    }

    [Fact]
    public void can_set_the_endpoint_name()
    {
        localQueue("one").Name.ShouldBe("one");
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
