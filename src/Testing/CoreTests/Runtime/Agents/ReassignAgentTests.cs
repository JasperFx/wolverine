using NSubstitute;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class ReassignAgentTests : IAsyncLifetime
{
    private List<object> theCascadingMessages;
    private readonly ReassignAgent theCommand = new(new Uri("blue://one"), Guid.NewGuid(), Guid.NewGuid());
    private readonly MockWolverineRuntime theRuntime = new();

    public async Task InitializeAsync()
    {
        var enumerable = theCommand.ExecuteAsync(theRuntime, CancellationToken.None);

        theCascadingMessages = new List<object>();
        await foreach (var message in enumerable) theCascadingMessages.Add(message);

        await theRuntime.Tracker.DrainAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task should_stop_the_currently_running_agent()
    {
        await theRuntime.Agents.Received().InvokeAsync(theCommand.OriginalNodeId, new StopAgent(theCommand.AgentUri));
    }

    [Fact]
    public void should_cascade_a_command_to_start_the_agent_on_next_node()
    {
        theCascadingMessages.Single().ShouldBe(new AssignAgent(theCommand.AgentUri, theCommand.ActiveNodeId));
    }

    [Fact]
    public void should_track_the_agent_was_stopped()
    {
        theRuntime.ReceivedEvents.Single().ShouldBe(new AgentStopped(theCommand.AgentUri));
    }
}