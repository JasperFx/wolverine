using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class StopRemoteAgentTests : IAsyncLifetime
{
    private AgentCommands theCascadingMessages;
    private readonly StopRemoteAgent theCommand = new(new Uri("blue://one"), new NodeDestination(Guid.NewGuid(), new Uri("control://one")));
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
        await theRuntime.Agents.Received().InvokeAsync(theCommand.Destination, new StopAgent(theCommand.AgentUri));
    }

}