using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class AgentCommandsTests
{
    [Fact]
    public void empty_cannot_be_observably_mutated()
    {
        // AgentCommands is a mutable List<IAgentCommand>, so Empty must hand out
        // a fresh instance per call — a shared singleton would be poisoned
        // process-wide by any caller that mutates the returned list
        var first = AgentCommands.Empty;
        first.Add(new StartAgent(new Uri("blue://one")));
        first.Pop();
        first.AddRange([new StartAgent(new Uri("blue://two"))]);

        var second = AgentCommands.Empty;
        second.ShouldBeEmpty();
        second.ShouldNotBeSameAs(first);
    }
}
