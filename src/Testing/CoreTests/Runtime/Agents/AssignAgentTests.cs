using NSubstitute;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class AssignAgentTests : IAsyncLifetime
{
    private AgentCommands theCascadingMessages;
    private readonly AssignAgent theCommand = new(new Uri("blue://one"), Guid.NewGuid());
    private readonly MockWolverineRuntime theRuntime = new();
    
    public async Task InitializeAsync()
    {
        theCascadingMessages = await theCommand.ExecuteAsync(theRuntime, CancellationToken.None);

        await theRuntime.Tracker.DrainAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task should_execute_start_agent_at_proper_node()
    {
        await theRuntime.Agents.Received().InvokeAsync(theCommand.NodeId, new StartAgent(theCommand.AgentUri));
    }

    [Fact]
    public void should_track_the_agent_started()
    {
        theRuntime.ReceivedEvents.Single()
            .ShouldBe(new AgentStarted(theCommand.NodeId, theCommand.AgentUri));
    }
}