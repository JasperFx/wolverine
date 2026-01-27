using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports;

public class BackPressureAgentTests
{
    private readonly BackPressureAgent theBackPressureAgent;
    private readonly Endpoint theEndpoint = new TcpEndpoint(5555);
    private readonly IListeningAgent theListeningAgent = Substitute.For<IListeningAgent>();
    private readonly IWolverineObserver theObserver;

    public BackPressureAgentTests()
    {
        theObserver = Substitute.For<IWolverineObserver>();
        theBackPressureAgent = new BackPressureAgent(theListeningAgent, theEndpoint, theObserver);
    }

    [Fact]
    public async Task do_nothing_when_accepting_and_under_the_threshold()
    {
        theListeningAgent.Status
            .Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount
            .Returns(theEndpoint.BufferingLimits.Maximum - 1);

        // Evaluate whether or not the listening should be paused
        // based on the current queued item count, the current status
        // of the listening agent, and the configured buffering limits
        // for the endpoint
        await theBackPressureAgent.CheckNowAsync();

        // Should decide NOT to do anything in this particular case
        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
    }

    [Fact]
    public async Task do_nothing_when_accepting_at_the_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Maximum);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
    }

    [Fact]
    public async Task stop_receiving_accepting_over_the_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Maximum + 1);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.Received().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();

        await theObserver.Received().BackPressureTriggered(theEndpoint, theListeningAgent);
    }

    [Fact]
    public async Task do_nothing_when_too_busy_and_over_the_restart_limit()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart + 1);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.DidNotReceive().StartAsync();
        
        await theObserver.DidNotReceive().BackPressureTriggered(theEndpoint, theListeningAgent);
    }

    [Fact]
    public async Task restart_when_too_busy_but_reached_the_restart_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.Received().StartAsync();
    }

    [Fact]
    public async Task restart_when_too_busy_but_below_the_restart_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart - 1);

        await theBackPressureAgent.CheckNowAsync();

        await theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        await theListeningAgent.Received().StartAsync();
    }
}