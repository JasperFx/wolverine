using CoreTests.Transports;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class WolverineNodeTests
{
    [Fact]
    public void create_from_wolverine_options()
    {
        var options = new WolverineOptions
        {
            ApplicationAssembly = GetType().Assembly
        };
        options.Transports.NodeControlEndpoint = new FakeEndpoint("fake://one".ToUri(), EndpointRole.System);

        var node = WolverineNode.For(options);

        node.NodeId.ShouldBe(options.UniqueNodeId);
        node.ControlUri.ShouldBe(options.Transports.NodeControlEndpoint.Uri);
        node.Version.ShouldBe(GetType().Assembly.GetName().Version);
    }

    [Fact]
    public void is_leader()
    {
        var node = new WolverineNode();
        node.IsLeader().ShouldBeFalse();

        node.ActiveAgents.Add(NodeAgentController.LeaderUri);
        node.IsLeader().ShouldBeTrue();
    }
}