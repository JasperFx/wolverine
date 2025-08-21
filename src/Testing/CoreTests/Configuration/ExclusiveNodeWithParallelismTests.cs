using System;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Configuration;

public class ExclusiveNodeWithParallelismTests
{
    [Fact]
    public void exclusive_node_with_parallelism_sets_correct_options()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config.ExclusiveNodeWithParallelism(5);
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        endpoint.MaxDegreeOfParallelism.ShouldBe(5);
        endpoint.ListenerCount.ShouldBe(1);
        endpoint.IsListener.ShouldBe(true);
    }

    [Fact]
    public void exclusive_node_with_parallelism_sets_endpoint_name()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config.ExclusiveNodeWithParallelism(5, "special-endpoint");
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.EndpointName.ShouldBe("special-endpoint");
    }

    [Fact]
    public void exclusive_node_with_parallelism_validates_max_parallelism()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        Should.Throw<ArgumentException>(() => config.ExclusiveNodeWithParallelism(0))
            .Message.ShouldContain("Maximum parallelism must be at least 1");
        
        Should.Throw<ArgumentException>(() => config.ExclusiveNodeWithParallelism(-1))
            .Message.ShouldContain("Maximum parallelism must be at least 1");
    }

    [Fact]
    public void exclusive_node_with_parallelism_throws_for_local_queue()
    {
        var endpoint = new LocalQueue("test");
        var config = new ListenerConfiguration<IListenerConfiguration, LocalQueue>(endpoint);
        
        Should.Throw<NotSupportedException>(() => config.ExclusiveNodeWithParallelism(5))
            .Message.ShouldContain("cannot use the ExclusiveNodeWithParallelism option for local queues");
    }

    [Fact]
    public void exclusive_node_with_session_ordering_sets_correct_options()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config.ExclusiveNodeWithSessionOrdering(3);
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        endpoint.MaxDegreeOfParallelism.ShouldBe(3);
        endpoint.ListenerCount.ShouldBe(3);
        endpoint.IsListener.ShouldBe(true);
    }

    [Fact]
    public void exclusive_node_with_session_ordering_sets_endpoint_name()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config.ExclusiveNodeWithSessionOrdering(3, "session-endpoint");
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.EndpointName.ShouldBe("session-endpoint");
    }

    [Fact]
    public void exclusive_node_with_session_ordering_validates_max_sessions()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        Should.Throw<ArgumentException>(() => config.ExclusiveNodeWithSessionOrdering(0))
            .Message.ShouldContain("Maximum parallel sessions must be at least 1");
        
        Should.Throw<ArgumentException>(() => config.ExclusiveNodeWithSessionOrdering(-1))
            .Message.ShouldContain("Maximum parallel sessions must be at least 1");
    }

    [Fact]
    public void exclusive_node_with_session_ordering_throws_for_local_queue()
    {
        var endpoint = new LocalQueue("test");
        var config = new ListenerConfiguration<IListenerConfiguration, LocalQueue>(endpoint);
        
        Should.Throw<NotSupportedException>(() => config.ExclusiveNodeWithSessionOrdering(3))
            .Message.ShouldContain("cannot use the ExclusiveNodeWithSessionOrdering option for local queues");
    }

    [Fact]
    public void can_chain_exclusive_node_with_other_configurations()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config
            .ExclusiveNodeWithParallelism(5)
            .UseDurableInbox()
            .TelemetryEnabled(false);
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.ListenerScope.ShouldBe(ListenerScope.Exclusive);
        endpoint.MaxDegreeOfParallelism.ShouldBe(5);
        endpoint.Mode.ShouldBe(EndpointMode.Durable);
        endpoint.TelemetryEnabled.ShouldBe(false);
    }

    [Fact]
    public void default_parallelism_is_10()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config.ExclusiveNodeWithParallelism();
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.MaxDegreeOfParallelism.ShouldBe(10);
    }

    [Fact]
    public void default_parallel_sessions_is_10()
    {
        var endpoint = new TcpEndpoint("localhost", 5000);
        var config = new ListenerConfiguration(endpoint);
        
        config.ExclusiveNodeWithSessionOrdering();
        ((IDelayedEndpointConfiguration)config).Apply();
        
        endpoint.MaxDegreeOfParallelism.ShouldBe(10);
        endpoint.ListenerCount.ShouldBe(10);
    }
}