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

    [Fact]
    public void StartAgents_with_no_agents()
    {
        var other = roundTrip(new StartAgents([]));

        other.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void AgentsStarted()
    {
        var command = new AgentsStarted([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void AgentsStarted_with_no_agents()
    {
        // GH-3460 -- StartAgents can legitimately report zero successfully
        // started agents during node churn
        var other = roundTrip(new AgentsStarted([]));

        other.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void StopAgents()
    {
        var command = new StopAgents([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void StopAgents_with_no_agents()
    {
        var other = roundTrip(new StopAgents([]));

        other.AgentUris.ShouldBeEmpty();
    }

    [Fact]
    public void AgentsStopped()
    {
        var command = new AgentsStopped([new Uri("fake://one"), new Uri("fake://two"), new Uri("fake://three")]);

        var other = roundTrip(command);

        other.AgentUris.ShouldBe(command.AgentUris);
    }

    [Fact]
    public void AgentsStopped_with_no_agents()
    {
        var other = roundTrip(new AgentsStopped([]));

        other.AgentUris.ShouldBeEmpty();
    }
}