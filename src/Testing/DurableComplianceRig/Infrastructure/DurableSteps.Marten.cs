using Bobcat;
using Bobcat.Engine;
using DurableComplianceRig.Stores;
using Wolverine;

namespace DurableComplianceRig.Infrastructure;

/// <summary>Steps for the Marten idempotency and lifecycle scenarios.</summary>
public sealed partial class DurableSteps
{
    [When("message {number} is sent again from node {node}")]
    public async Task MessageIsSentAgain(int number, int node)
    {
        // RigIds.Stamp gives the second envelope the same id as the first: to the receiver it is a redelivery
        await Node(node).MessageBus().PublishAsync(new RigMessage(MessageNumbered(number)));
    }

    [When("message {number} has committed its effect")]
    public async Task MessageHasCommitted(int number)
    {
        var id = MessageNumbered(number);
        var committed = await pollAsync(async () => (await Store.SnapshotAsync()).Ledger.Any(x => x.MessageId == id),
            30.Seconds());
        if (!committed) throw new SpecCriticalException($"Message {number} never committed; the rest of the scenario depends on it");
    }

    [Then("message {number} reached the handler chain {count} times")]
    public void MessageReachedTheChain(int number, int count)
    {
        // Counted ahead of the idempotency check, so a duplicate that never arrived cannot pass for one that was
        // dropped
        SpecAssert.Check($"deliveries of message {number} that reached the handler chain",
            MartenRigHooks.DeliveriesOf(MessageNumbered(number)), count);
    }

    [Given("message {number} reschedules itself the first time and returns normally")]
    public void MessageReschedulesItself(int number) => MartenRigHooks.RescheduleFirst(MessageNumbered(number), 1);

    [Given("message {number} holds after its first commit")]
    public void MessageHoldsAfterCommit(int number) => MartenRigHooks.HoldAfterFirstCommit(MessageNumbered(number));

    [When("message {number} has committed and is held before its row is settled")]
    public async Task MessageIsHeldAfterCommit(int number)
    {
        var id = MessageNumbered(number);
        var held = await pollAsync(() => Task.FromResult(MartenRigHooks.IsHeld(id)), 30.Seconds());
        if (!held) throw new SpecCriticalException($"Message {number} never reached its post-commit hold");
    }
}
