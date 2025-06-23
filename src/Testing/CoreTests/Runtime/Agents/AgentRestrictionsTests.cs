using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class AgentRestrictionsTests
{
    [Fact]
    public void add_single_pin_restriction()
    {
        var restrictions = new AgentRestrictions([]);
        
        restrictions.PinAgent(new Uri("fake://one"), 3);

        var restriction = restrictions.FindChanges().Single();
        restriction.NodeNumber.ShouldBe(3);
        restriction.Type.ShouldBe(AgentRestrictionType.Pinned);
        restriction.AgentUri.ShouldBe(new Uri("fake://one"));
    }
    
    [Fact]
    public void change_single_pin_restriction()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://two"), AgentRestrictionType.Pinned, 5);
        var restrictions = new AgentRestrictions([original]);
        
        restrictions.PinAgent(original.AgentUri, 10);

        var restriction = restrictions.FindChanges().Single();
        restriction.NodeNumber.ShouldBe(10);
        restriction.Type.ShouldBe(AgentRestrictionType.Pinned);
        restriction.AgentUri.ShouldBe(restriction.AgentUri);
        restriction.Id.ShouldBe(original.Id);
    }

    [Fact]
    public void change_single_pin_with_no_change()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://two"), AgentRestrictionType.Pinned, 5);
        var restrictions = new AgentRestrictions([original]);
        
        restrictions.PinAgent(original.AgentUri, original.NodeNumber);
        
        restrictions.FindChanges().Any().ShouldBeFalse();
    }

    [Fact]
    public void add_single_pause_restriction()
    {
        var restrictions = new AgentRestrictions([]);
        
        restrictions.PauseAgent(new Uri("fake://one"));

        var restriction = restrictions.FindChanges().Single();
        restriction.Type.ShouldBe(AgentRestrictionType.Paused);
        restriction.AgentUri.ShouldBe(new Uri("fake://one"));
    }
    
    [Fact]
    public void change_single_pause_restriction()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://two"), AgentRestrictionType.Paused, 0);
        var restrictions = new AgentRestrictions([original]);
        
        restrictions.PauseAgent(original.AgentUri);

        restrictions.FindChanges().Any().ShouldBeFalse();
    }

    [Fact]
    public void restart_an_agent_that_is_not_paused()
    {
        var restrictions = new AgentRestrictions([]);
        restrictions.RestartAgent(new Uri("fake://two"));
        restrictions.FindChanges().Any().ShouldBeFalse();
    }

    [Fact]
    public void restart_a_paused_agent()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://two"), AgentRestrictionType.Paused, 0);
        var restrictions = new AgentRestrictions([original]);
        restrictions.RestartAgent(original.AgentUri);

        var changed = restrictions.FindChanges().Single();
        
        changed.AgentUri.ShouldBe(original.AgentUri);
        changed.Id.ShouldBe(original.Id);
        changed.Type.ShouldBe(AgentRestrictionType.None);
    }

    [Fact]
    public void start_paused_restart_then_pause_again_because_someone_will_make_that_happen()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://two"), AgentRestrictionType.Paused, 0);
        var restrictions = new AgentRestrictions([original]);
        
        restrictions.RestartAgent(original.AgentUri);
        restrictions.PauseAgent(original.AgentUri);

        restrictions.FindChanges().Any().ShouldBeFalse();
    }

    [Fact]
    public void remove_pin_nothing_there()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://three"), AgentRestrictionType.Pinned, 5);
        var assignments = new AgentRestrictions([original]);
        
        assignments.RemovePin(new Uri("fake://four"));

        assignments.FindChanges().Any().ShouldBeFalse();
    }

    [Fact]
    public void remove_pin_hit()
    {
        var original = new AgentRestriction(Guid.NewGuid(), new Uri("fake://three"), AgentRestrictionType.Pinned, 5);
        var assignments = new AgentRestrictions([original]);
        
        assignments.RemovePin(original.AgentUri);

        var changed = assignments.FindChanges().Single();
        changed.NodeNumber.ShouldBe(0);
        changed.Id.ShouldBe(original.Id);
        changed.Type.ShouldBe(AgentRestrictionType.None);
        changed.AgentUri.ShouldBe(original.AgentUri);
    }

}