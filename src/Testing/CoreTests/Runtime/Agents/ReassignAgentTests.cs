using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class ReassignAgentTests : IAsyncLifetime
{
    private AgentCommands theCascadingMessages;
    private readonly ReassignAgent theCommand = new(new Uri("blue://one"), NodeDestination.Standin(), NodeDestination.Standin());
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
    public async Task should_stop_the_currently_running_agent()
    {
        await theRuntime.Agents.Received().InvokeAsync(theCommand.OriginalNode, new StopAgent(theCommand.AgentUri));
    }

    [Fact]
    public void should_cascade_a_command_to_start_the_agent_on_next_node()
    {
        theCascadingMessages.Single().ShouldBe(new AssignAgent(theCommand.AgentUri, theCommand.ActiveNode));
    }

}