using Bobcat;
using Bobcat.Engine;
using DurableComplianceRig.Stores;
using Wolverine;

namespace DurableComplianceRig.Infrastructure;

/// <summary>Steps for the EF Core store: transaction-level faults, duplicate deliveries, and what the commit saw.</summary>
public sealed partial class DurableSteps
{
    [Given("message {number} fails in SaveChanges every time and should be discarded")]
    public void MessageFailsInSaveChangesAndIsDiscarded(int number)
    {
        var id = MessageNumbered(number);
        EfScript.Set(id, EfFault.BreakSaveChanges);
        _expected[id] = Outcome.Discarded;
    }

    [Given("message {number} fails as its EF Core transaction commits every time and should be discarded")]
    public void MessageFailsAtCommitAndIsDiscarded(int number)
    {
        var id = MessageNumbered(number);
        EfScript.Set(id, EfFault.FailCommitDiscarded);
        _expected[id] = Outcome.Discarded;
    }

    /// <summary>Only the first commit fails: a later run of the discarded message would commit an effect the checker sees.</summary>
    [Given("message {number} fails as its EF Core transaction commits the first time and should be discarded")]
    public void MessageFailsAtCommitOnceAndIsDiscarded(int number)
    {
        var id = MessageNumbered(number);
        EfScript.Set(id, EfFault.FailCommitDiscarded, EfFault.None);
        _expected[id] = Outcome.Discarded;
    }

    [Given("message {number} fails as its EF Core transaction commits every time and should be dead lettered")]
    public void MessageFailsAtCommitAndIsDeadLettered(int number)
    {
        var id = MessageNumbered(number);
        EfScript.Set(id, EfFault.FailCommit);
        _expected[id] = Outcome.DeadLettered;
    }

    [Given("message {number} fails as its EF Core transaction commits the first {failures} time(s)")]
    public void MessageFailsAtCommitFirst(int number, int failures)
    {
        var faults = Enumerable.Repeat(EfFault.FailCommit, failures).Append(EfFault.None).ToArray();
        EfScript.Set(MessageNumbered(number), faults);
    }

    [Given("the inbox row is read on each EF Core transaction just before it commits")]
    public void TheInboxIsObservedAtCommit() => EfScript.ObserveInbox = true;

    [When("message {number} has committed its effect")]
    public async Task MessageHasCommitted(int number)
    {
        var id = MessageNumbered(number);
        var committed = await pollAsync(async () => (await Store.SnapshotAsync()).Ledger.Any(x => x.MessageId == id),
            30.Seconds());
        if (!committed) throw new SpecCriticalException($"Message {number} never committed; the rest of the scenario depends on it");
    }

    /// <summary>The same message id, so <see cref="RigIds.Stamp" /> gives the copy the same envelope id: a redelivery.</summary>
    [When("message {number} is delivered again from node {node}")]
    public async Task MessageIsDeliveredAgain(int number, int node)
    {
        await Node(node).MessageBus().PublishAsync(new RigMessage(MessageNumbered(number)));
    }

    /// <summary>Narration, not a step: the envelope status each scripted commit failure saw (the F25 window).</summary>
    public void LogEfCommitFailures()
    {
        foreach (var status in EfScript.StatusAtFailedCommit)
        {
            SpecLog.Write($"envelope status as the commit failed: {status}");
            _output.WriteLine($"envelope status as the commit failed: {status}");
        }
    }

    /// <summary>Narration, not a step: every inbox status read on a committing transaction.</summary>
    public void LogInboxStatusAtCommit()
    {
        foreach (var (id, statuses) in EfScript.InboxStatusAtCommit)
        {
            SpecLog.Write($"inbox row of {id} as each commit ran: {statuses.Join(", ")}");
            _output.WriteLine($"inbox row of {id} as each commit ran: {statuses.Join(", ")}");
        }
    }

    [Then("every message's inbox row read Handled inside its EF Core transaction")]
    public void InboxRowsWereHandledInsideTheTransaction()
    {
        var wrong = new List<string>();
        foreach (var id in _expected.Where(x => x.Value == Outcome.Handled).Select(x => x.Key))
        {
            if (!EfScript.InboxStatusAtCommit.TryGetValue(id, out var statuses) || statuses.IsEmpty)
            {
                wrong.Add($"{id}: no commit observed");
            }
            else if (statuses.Last() != "Handled")
            {
                wrong.Add($"{id}: {statuses.Join(", ")}");
            }
        }

        SpecAssert.Check("inbox rows not Handled as the EF Core transaction committed", describe(wrong), "none");
    }
}
