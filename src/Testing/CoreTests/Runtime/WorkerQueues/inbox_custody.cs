using JasperFx.Core;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.WorkerQueues;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

public class inbox_custody
{
    private readonly InboxCustody theCustody = new();
    private readonly DateTimeOffset theStart = DateTimeOffset.UtcNow;

    [Fact]
    public void an_envelope_is_held_until_it_leaves()
    {
        var envelope = ObjectMother.Envelope();

        theCustody.Enter(envelope, theStart);
        theCustody.Count.ShouldBe(1);

        theCustody.Leave(envelope);
        theCustody.Count.ShouldBe(0);
    }

    [Fact]
    public void a_requeued_envelope_stays_held_after_the_execution_that_requeued_it_ends()
    {
        var envelope = ObjectMother.Envelope();

        theCustody.Enter(envelope, theStart);

        // The pipeline requeues it, then the failed execution finishes
        theCustody.Enter(envelope, theStart);
        theCustody.Leave(envelope);
        theCustody.Count.ShouldBe(1);

        // The requeued execution finishes
        theCustody.Leave(envelope);
        theCustody.Count.ShouldBe(0);
    }

    [Fact]
    public void leaving_an_envelope_that_was_never_held_is_harmless()
    {
        theCustody.Leave(ObjectMother.Envelope());
        theCustody.Count.ShouldBe(0);
    }

    [Fact]
    public void only_envelopes_held_for_at_least_a_period_are_due()
    {
        var old = ObjectMother.Envelope();
        var fresh = ObjectMother.Envelope();

        theCustody.Enter(old, theStart);
        theCustody.Enter(fresh, theStart.Add(10.Seconds()));

        theCustody.FindDue(theStart.Add(5.Seconds())).ShouldBe([old]);
    }

    [Fact]
    public void a_restarted_clock_is_not_due_again_until_another_period_passes()
    {
        var envelope = ObjectMother.Envelope();
        theCustody.Enter(envelope, theStart);

        // FindDue takes "now minus one period"; the period here is 5 seconds
        var due = theCustody.FindDue(theStart);
        theCustody.MarkClockRestarted(due, theStart.Add(5.Seconds()));

        // 6 seconds in: restarted at 5, so not due until 10
        theCustody.FindDue(theStart.Add(1.Seconds())).ShouldBeEmpty();
        theCustody.FindDue(theStart.Add(5.Seconds())).ShouldBe([envelope]);
    }

    [Fact]
    public void a_failed_restart_is_due_again_on_the_next_tick()
    {
        var envelope = ObjectMother.Envelope();
        theCustody.Enter(envelope, theStart);

        // Found due, but the write failed, so it is never marked
        theCustody.FindDue(theStart.Add(5.Seconds())).ShouldBe([envelope]);

        theCustody.FindDue(theStart.Add(6.Seconds())).ShouldBe([envelope]);
    }
}
