using JasperFx;
using NSubstitute;
using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

// CompositeAgent.StopAsync() used to leave its status as Running after stopping every inner
// agent, so a stopped composite still reported itself as running.
public class composite_agent_status
{
    private static CompositeAgent BuildComposite(out IAgent first, out IAgent second)
    {
        first = Substitute.For<IAgent>();
        second = Substitute.For<IAgent>();
        return new CompositeAgent(new Uri("composite://one"), [first, second]);
    }

    [Fact]
    public void starts_out_stopped()
    {
        var composite = BuildComposite(out _, out _);
        composite.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task is_running_after_start()
    {
        var composite = BuildComposite(out var first, out var second);

        await composite.StartAsync(CancellationToken.None);

        composite.Status.ShouldBe(AgentStatus.Running);
        await first.Received().StartAsync(Arg.Any<CancellationToken>());
        await second.Received().StartAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task is_stopped_after_stop()
    {
        var composite = BuildComposite(out var first, out var second);

        await composite.StartAsync(CancellationToken.None);
        await composite.StopAsync(CancellationToken.None);

        composite.Status.ShouldBe(AgentStatus.Stopped);
        await first.Received().StopAsync(Arg.Any<CancellationToken>());
        await second.Received().StopAsync(Arg.Any<CancellationToken>());
    }
}
