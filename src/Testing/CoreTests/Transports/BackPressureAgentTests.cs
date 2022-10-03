using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports;

public class BackPressureAgentTests
{
    private readonly IListeningAgent theListeningAgent = Substitute.For<IListeningAgent>();
    private readonly Endpoint theEndpoint = new TcpEndpoint(5555);
    private readonly BackPressureAgent theBackPressureAgent;

    public BackPressureAgentTests()
    {
        theBackPressureAgent = new BackPressureAgent(theListeningAgent, theEndpoint);
    }

    [Fact]
    public void do_nothing_when_accepting_and_under_the_threshold()
    {
        theListeningAgent.Status
            .Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount
            .Returns(theEndpoint.BufferingLimits.Maximum - 1);
        
        // Evaluate whether or not the listening should be paused
        // based on the current queued item count, the current status
        // of the listening agent, and the configured buffering limits
        // for the endpoint
        theBackPressureAgent.CheckNowAsync();

        // Should decide NOT to do anything in this particular case
        theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        theListeningAgent.DidNotReceive().StartAsync();
    }
    
    [Fact]
    public void do_nothing_when_accepting_at_the_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Maximum);
        
        theBackPressureAgent.CheckNowAsync();

        theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        theListeningAgent.DidNotReceive().StartAsync();
    }
    
        
    [Fact]
    public void stop_receiving_accepting_over_the_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.Accepting);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Maximum + 1);
        
        theBackPressureAgent.CheckNowAsync();

        theListeningAgent.Received().MarkAsTooBusyAndStopReceivingAsync();
        theListeningAgent.DidNotReceive().StartAsync();
    }
    
            
    [Fact]
    public void do_nothing_when_too_busy_and_over_the_restart_limit()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart + 1);
        
        theBackPressureAgent.CheckNowAsync();

        theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        theListeningAgent.DidNotReceive().StartAsync();
    }

    [Fact]
    public void restart_when_too_busy_but_reached_the_restart_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart);
        
        theBackPressureAgent.CheckNowAsync();

        theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        theListeningAgent.Received().StartAsync();
    }
    
    
    [Fact]
    public void restart_when_too_busy_but_below_the_restart_threshold()
    {
        theListeningAgent.Status.Returns(ListeningStatus.TooBusy);
        theListeningAgent.QueueCount.Returns(theEndpoint.BufferingLimits.Restart - 1);
        
        theBackPressureAgent.CheckNowAsync();

        theListeningAgent.DidNotReceive().MarkAsTooBusyAndStopReceivingAsync();
        theListeningAgent.Received().StartAsync();
    }
    
}