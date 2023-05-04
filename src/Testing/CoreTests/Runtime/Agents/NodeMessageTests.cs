using System.Security.Cryptography.X509Certificates;
using JasperFx.Core.Reflection;
using NSubstitute;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class NodeMessageTests
{
    [Fact]
    public async Task send_to_exact_node()
    {
        var context = Substitute.For<IMessageContext>();
        var endpoint = Substitute.For<IDestinationEndpoint>();

        var node = new WolverineNode
        {
            ControlUri = new Uri("transport://one")
        };

        context.EndpointFor(node.ControlUri).Returns(endpoint);

        var original = new StartAgent(new Uri("blue://one"));

        var message = original.ToNode(node);

        await message.As<ISendMyself>().ApplyAsync(context);

        await endpoint.Received().SendAsync(original);
    }
    
}