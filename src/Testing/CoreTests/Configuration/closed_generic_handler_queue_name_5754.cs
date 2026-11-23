using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5754. A closed generic handler type beside another handler type for the same message gets a local queue
/// named for its handler type. That name has to be a valid local:// Uri host, and two closings of the same
/// generic handler must not share a queue.
/// </summary>
public class closed_generic_handler_queue_name_5754
{
    private static async Task<IHost> hostWith(params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);
            }).StartAsync();
    }

    [Fact]
    public async Task a_closed_generic_handler_beside_another_handler_starts_and_handles_the_message()
    {
        using var host = await hostWith(typeof(PlainPingHandler5754), typeof(RelayHandler5754<Ping5754>));

        var chains = host.GetRuntime().Handlers.AllChains().Where(x => x.MessageType == typeof(Ping5754)).ToArray();
        chains.Length.ShouldBe(2);

        foreach (var chain in chains)
        {
            var handlerType = chain.Handlers.ShouldHaveSingleItem().HandlerType;
            var queue = chain.Endpoints.ShouldHaveSingleItem().ShouldBeOfType<LocalQueue>();
            queue.EndpointName.ShouldBe(LocalTransport.QueueNameForHandlerType(handlerType));
            queue.Uri.ShouldBe(new Uri("local://" + queue.EndpointName));
        }

        var session = await host.SendMessageAndWaitAsync(new Ping5754("one"));
        session.Executed.Envelopes().Count().ShouldBe(2);
    }

    [Fact]
    public void the_queue_name_of_a_non_generic_handler_type_is_unchanged()
    {
        // These names are persisted in durable inboxes, so they must not move
        LocalTransport.QueueNameForHandlerType(typeof(PlainPingHandler5754))
            .ShouldBe(typeof(PlainPingHandler5754).FullNameInCode().ToLowerInvariant());
        LocalTransport.QueueNameForHandlerType(typeof(Outer5754.NestedPingHandler))
            .ShouldBe(typeof(Outer5754.NestedPingHandler).FullNameInCode().ToLowerInvariant());
    }

    [Fact]
    public void the_queue_name_of_a_closed_generic_handler_type_is_readable_and_a_valid_uri()
    {
        LocalTransport.QueueNameForHandlerType(typeof(RelayHandler5754<Ping5754>))
            .ShouldBe("coretests.configuration.relayhandler5754-of-coretests.configuration.ping5754-end");
    }

    [Theory]
    [InlineData(typeof(RelayHandler5754<Ping5754>))]
    [InlineData(typeof(RelayHandler5754<Pong5754>))]
    [InlineData(typeof(RelayHandler5754<int[]>))]
    [InlineData(typeof(RelayHandler5754<int?>))]
    [InlineData(typeof(RelayHandler5754<RelayHandler5754<Ping5754>>))]
    [InlineData(typeof(PairHandler5754<Ping5754, Pong5754>))]
    [InlineData(typeof(PairHandler5754<RelayHandler5754<Ping5754>, Pong5754>))]
    [InlineData(typeof(PairHandler5754<RelayHandler5754<Ping5754, Pong5754>>))]
    [InlineData(typeof(Outer5754.NestedRelayHandler<Ping5754>))]
    public void every_closed_generic_queue_name_makes_a_valid_local_uri(Type handlerType)
    {
        var name = LocalTransport.QueueNameForHandlerType(handlerType);
        var queue = new LocalQueue(name);
        queue.EndpointName.ShouldBe(name);
        queue.Uri.Host.ShouldBe(name);
    }

    [Fact]
    public void different_closings_of_a_generic_handler_get_different_queue_names()
    {
        Type[] handlerTypes =
        [
            typeof(RelayHandler5754<Ping5754>),
            typeof(RelayHandler5754<Pong5754>),
            typeof(RelayHandler5754<int[]>),
            typeof(RelayHandler5754<int>),
            typeof(RelayHandler5754<int?>),
            typeof(RelayHandler5754<RelayHandler5754<Ping5754>>),
            typeof(PairHandler5754<Ping5754, Pong5754>),
            typeof(PairHandler5754<Pong5754, Ping5754>),
            typeof(PairHandler5754<RelayHandler5754<Ping5754>, Pong5754>),
            typeof(PairHandler5754<RelayHandler5754<Ping5754, Pong5754>>),
            typeof(Outer5754.NestedRelayHandler<Ping5754>)
        ];

        handlerTypes.Select(LocalTransport.QueueNameForHandlerType).Distinct().Count().ShouldBe(handlerTypes.Length);
    }
}

public record Ping5754(string Name);

public record Pong5754(string Name);

public class PlainPingHandler5754
{
    public static void Handle(Ping5754 ping)
    {
    }
}

public class RelayHandler5754<T>
{
    public static void Handle(T message)
    {
    }
}

public class RelayHandler5754<T1, T2>
{
}

public class PairHandler5754<T1, T2>
{
}

public class PairHandler5754<T>
{
}

public class Outer5754
{
    public class NestedPingHandler
    {
    }

    public class NestedRelayHandler<T>
    {
    }
}
