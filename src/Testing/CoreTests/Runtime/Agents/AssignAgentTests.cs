using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class AssignAgentTests : IAsyncLifetime
{
    private AgentCommands theCascadingMessages;
    private readonly AssignAgent theCommand = new(new Uri("blue://one"), NodeDestination.Standin());
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
        await theRuntime.Agents.Received().InvokeAsync(theCommand.Destination, new StartAgent(theCommand.AgentUri));
    }

}