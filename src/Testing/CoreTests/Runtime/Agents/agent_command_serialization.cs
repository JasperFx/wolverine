using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class agent_command_serialization
{
    protected T roundTrip<T>(T command) where T : IAgentCommand, ISerializable
    {
        var serializer = new IntrinsicSerializer<T>();
        var bytes = serializer.Write(new Envelope(command));
        return (T)serializer.ReadFromData(bytes);
    }

    [Fact]
    public void StartAgents()
    {
        var command = new StartAgents([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);
        
        other.AgentUris.ShouldBe(command.AgentUris);
    }
}