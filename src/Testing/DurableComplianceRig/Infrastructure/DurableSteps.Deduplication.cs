using Bobcat;
using Bobcat.Engine;
using DurableComplianceRig.Stores;
using Wolverine;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// Steps for logical deduplication. An intent is a key; its copies are ordinary rig messages (numbered with
/// everything else) that share that key as their deduplication id, each with its own envelope id. A copy's outcome
/// is <see cref="Outcome.Deduplicated" /> unless a step pins it, and the copies of an intent are judged together.
/// </summary>
public sealed partial class DurableSteps
{
    private readonly Dictionary<string, List<Guid>> _intents = [];
    private readonly Dictionary<Guid, string> _keyOf = [];
    private readonly HashSet<Guid> _sentCopies = [];
    private RigSnapshot? _settledSnapshot;

    /// <summary>
    /// Appended to every key, so no claim left by an earlier spec or run (claims outlive ResetResourceState's inbox
    /// wipe by design) can refuse this spec's first copy. Hex digits only, identical on every key of the spec, so
    /// case and accent variants of a key stay variants of each other.
    /// </summary>
    public string KeySuffix { get; } = "~" + Guid.NewGuid().ToString("N")[..10];

    public MessageDeduplicationMode DeduplicationMode { get; set; } = MessageDeduplicationMode.CompareByHash;

    private DeduplicationProbe probe => new(Store.Databases[0], DeduplicationMode);

    private string keyFor(string intent) => intent + KeySuffix;

    private IReadOnlyList<Guid> copiesOf(string intent) =>
        _intents.TryGetValue(intent, out var copies)
            ? copies
            : throw new SpecCriticalException($"There is no intent keyed '{intent}'");

    // ---------------------------------------------------------------------------------------------------------
    // Arranging copies

    [Given("{count} copies of an intent keyed {intent}")]
    public void CopiesOfAnIntent(int count, string intent)
    {
        if (!_intents.TryGetValue(intent, out var copies)) _intents[intent] = copies = [];

        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            _messages.Add(id);
            _expected[id] = Outcome.Deduplicated;
            copies.Add(id);
            _keyOf[id] = keyFor(intent);
        }
    }

    [Given("{intents} intents with {copies} copies each")]
    public void IntentsWithCopiesEach(int intents, int copies)
    {
        for (var i = 1; i <= intents; i++) CopiesOfAnIntent(copies, $"intent-{i}");
    }

    [Given("every copy takes {milliseconds} ms in its handler")]
    public void EveryCopyTakes(int milliseconds)
    {
        foreach (var id in _keyOf.Keys) Script.Set(id, Step.Wait(milliseconds.Milliseconds()));
    }

    /// <summary>The copy, specifically, has to commit the intent's effect: no other copy can stand in for it.</summary>
    [Given("message {number} must commit the effect")]
    public void MessageMustCommit(int number) => _expected[MessageNumbered(number)] = Outcome.Handled;

    [Given("message {number} fails every time, without being judged as an ordinary dead letter")]
    public void CopyAlwaysFails(int number) => Script.Set(MessageNumbered(number), Step.Throw);

    [Given("message {number} fails after writing its effect every time and should be dead lettered")]
    public void MessageAlwaysFailsBeforeCommit(int number)
    {
        var id = MessageNumbered(number);
        Script.Set(id, Step.ThrowAfterLedgerWrite);
        _expected[id] = Outcome.DeadLettered;
    }

    [Given("message {number} holds in its handler, claim taken, until let go")]
    public void CopyHoldsInItsHandler(int number) => DeduplicationHooks.HoldFirstRun(MessageNumbered(number));

    [Given("each handler records whether its claim is visible to another connection")]
    public void HandlersProbeTheirClaims() => DeduplicationHooks.ProbeClaims = true;

    // ---------------------------------------------------------------------------------------------------------
    // Sending

    private async Task sendCopyAsync(Guid id, int node)
    {
        await Node(node).MessageBus().PublishAsync(new RigDeduplicatedMessage(id, _keyOf[id]));
        _sentCopies.Add(id);
    }

    [When("the copies are sent from node {node}")]
    public async Task TheCopiesAreSent(int node)
    {
        foreach (var id in _messages.Where(x => _keyOf.ContainsKey(x) && !_sentCopies.Contains(x)).ToArray())
        {
            await sendCopyAsync(id, node);
        }
    }

    [When("the copies are sent from nodes {first} and {second} at once")]
    public async Task TheCopiesAreSentFromTwoNodes(int first, int second)
    {
        var unsent = _messages.Where(x => _keyOf.ContainsKey(x) && !_sentCopies.Contains(x)).ToArray();
        await Task.WhenAll(
            Task.Run(async () =>
            {
                foreach (var id in unsent.Where((_, i) => i % 2 == 0)) await sendCopyAsync(id, first);
            }),
            Task.Run(async () =>
            {
                foreach (var id in unsent.Where((_, i) => i % 2 == 1)) await sendCopyAsync(id, second);
            }));
    }

    [When("message {number} is sent from node {node}")]
    public Task CopyIsSent(int number, int node) => sendCopyAsync(MessageNumbered(number), node);

    [When("message {number} is holding in its handler")]
    public async Task CopyIsHolding(int number)
    {
        var id = MessageNumbered(number);
        if (!await pollAsync(() => Task.FromResult(DeduplicationHooks.IsHeld(id)), 30.Seconds()))
        {
            throw new SpecCriticalException($"Message {number} never reached its hold");
        }
    }

    [When("message {number} is let go")]
    public void CopyIsLetGo(int number) => DeduplicationHooks.LetGo(MessageNumbered(number));

    [When("message {number} has been dead lettered")]
    public async Task MessageHasBeenDeadLettered(int number)
    {
        var id = MessageNumbered(number);
        var deadLettered = await pollAsync(async () => (await Store.SnapshotAsync()).DeadLetters.Any(x => x.Id == id),
            SettleTimeout);
        if (!deadLettered) throw new SpecCriticalException($"Message {number} was never dead lettered");
    }

    /// <summary>Long enough for a copy to be received and either refused or blocked behind the held one.</summary>
    [When("{milliseconds} ms pass")]
    public Task TimePasses(int milliseconds) => Task.Delay(milliseconds.Milliseconds());

    [When("the claim for {intent} has been reaped")]
    public async Task TheClaimHasBeenReaped(string intent)
    {
        var key = keyFor(intent);
        if (!await pollAsync(async () => await probe.CommittedClaimsAsync(key) == 0, 60.Seconds()))
        {
            throw new SpecCriticalException($"The claim for {intent} was never reaped");
        }
    }

    /// <summary>
    /// The crash signal first, then the shared crash: see <see cref="DeduplicationHooks.BeforeHandlingAsync" /> for
    /// which zombie handlers stop holding and why.
    /// </summary>
    [When("node {node} crashes with a copy in flight")]
    public async Task NodeCrashesWithACopyInFlight(int node)
    {
        DeduplicationHooks.NodeCrashed($"node {node}");
        NodeCrashes(node);

        // Decisive experiment for a claim stranded by the crash: give it back by hand, as the release would have
        if (Environment.GetEnvironmentVariable("RIG_DEDUP_RELEASE_ON_CRASH") == "1")
        {
            await probe.DeleteClaimsEndingWithAsync(KeySuffix);
            SpecLog.Write("RIG_DEDUP_RELEASE_ON_CRASH: deleted this spec's claims after the crash");
        }
    }

    /// <summary>
    /// The shared settle cannot tell when a copy's work is over: an intent's effect may belong to any copy, and on a
    /// broker a copy has no row until it arrives. So: every sent copy has a settled row (Handled) or a dead letter.
    /// </summary>
    [When("every copy has settled")]
    public async Task EveryCopyHasSettled()
    {
        var settled = await pollAsync(async () =>
        {
            var snapshot = await Store.SnapshotAsync();
            return _sentCopies.All(id =>
                snapshot.DeadLetters.Any(x => x.Id == id) ||
                (snapshot.Inbox.Any(x => x.Id == id) && snapshot.Inbox.Where(x => x.Id == id).All(x => x.Status == "Handled")));
        }, SettleTimeout);

        if (!settled) SpecLog.Write($"the copies did not settle within {SettleTimeout.TotalSeconds:0}s; judging them as they stand");

        await Task.Delay(QuietPeriod);
        _settledSnapshot = await Store.SnapshotAsync();

        foreach (var (intent, copies) in _intents)
        {
            var line = $"{intent}: " + copies.Select(id =>
                $"#{_messages.IndexOf(id) + 1} effects={_settledSnapshot.Ledger.Count(x => x.MessageId == id)} dlq={_settledSnapshot.DeadLetters.Count(x => x.Id == id)} runs={Script.ExecutionsOf(id)} rows={_settledSnapshot.Inbox.Where(x => x.Id == id).Select(x => x.Status).Join("/")}").Join(", ");
            SpecLog.Write(line);
            _output.WriteLine(line);

            // A passing test's output is hidden, and the narration is the result for the undocumented scenarios
            if (Environment.GetEnvironmentVariable("RIG_DEDUP_NARRATE") is { Length: > 0 } narration)
            {
                await File.AppendAllTextAsync(narration,
                    $"{TestContext.Current.Test?.TestDisplayName}: {line}{Environment.NewLine}", TestContext.Current.CancellationToken);
            }
        }

        foreach (var dead in _settledSnapshot.DeadLetters.Where(x => _keyOf.ContainsKey(x.Id)))
        {
            var line = $"dead letter #{_messages.IndexOf(dead.Id) + 1}: {await probe.DeadLetterExceptionAsync(dead.Id)}";
            SpecLog.Write(line);
            _output.WriteLine(line);
            if (Environment.GetEnvironmentVariable("RIG_DEDUP_NARRATE") is { Length: > 0 } narration)
            {
                await File.AppendAllTextAsync(narration,
                    $"{TestContext.Current.Test?.TestDisplayName}: {line}{Environment.NewLine}", TestContext.Current.CancellationToken);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Judging intents

    private RigSnapshot settledSnapshot =>
        _settledSnapshot ?? throw new SpecCriticalException("Judge intents only after 'every copy has settled'");

    private int effectsOf(IEnumerable<string> intents) =>
        intents.SelectMany(copiesOf).Sum(id => settledSnapshot.Ledger.Count(x => x.MessageId == id));

    private int deadLettersOf(IEnumerable<string> intents) =>
        intents.SelectMany(copiesOf).Sum(id => settledSnapshot.DeadLetters.Count(x => x.Id == id));

    private static string[] split(string intents) => intents.Split(',', StringSplitOptions.TrimEntries);

    [Then("the intent(s) {intents} committed {count} effect(s) between them")]
    public void TheIntentsCommittedEffects(string intents, int count) =>
        SpecAssert.Check($"effects committed by the copies of {intents}", effectsOf(split(intents)), count);

    [Then("every intent committed exactly one effect")]
    public void EveryIntentCommittedOneEffect()
    {
        var wrong = _intents.Keys.Select(x => (intent: x, effects: effectsOf([x]))).Where(x => x.effects != 1)
            .Select(x => $"{x.intent} x{x.effects}").ToArray();
        SpecAssert.Check("intents without exactly one effect", wrong.Length == 0 ? "none" : wrong.Join(", "), "none");
    }

    [Then("no copy was dead lettered")]
    public void NoCopyWasDeadLettered()
    {
        var wrong = _intents.Where(x => deadLettersOf([x.Key]) > 0).Select(x => x.Key).ToArray();
        SpecAssert.Check("intents with a dead-lettered copy", wrong.Length == 0 ? "none" : wrong.Join(", "), "none");
    }

    /// <summary>
    /// The weakest promise worth making when a copy fails while another is in flight: never two effects, and never
    /// nothing at all without a dead letter to show for it.
    /// </summary>
    [Then("the intent {intent} was done once, or its failure is in the dead letters")]
    public void TheIntentWasDoneOnceOrDeadLettered(string intent)
    {
        var effects = effectsOf([intent]);
        var deadLetters = deadLettersOf([intent]);
        SpecLog.Write($"{intent}: {effects} effect(s), {deadLetters} dead letter(s)");
        SpecAssert.Fact(effects <= 1, $"the copies of {intent} committed at most one effect");
        SpecAssert.Fact(effects == 1 || deadLetters > 0, $"{intent} was done, or its failure was dead lettered");
    }

    [Then("the store holds {count} claim(s) for {intent}")]
    public async Task TheStoreHoldsClaims(int count, string intent) =>
        SpecAssert.Check($"claim rows for {intent}", await probe.CommittedClaimsAsync(keyFor(intent)), count);

    [Then("while message {number} ran, its claim was visible to another connection: {visible}")]
    public void TheClaimWasVisibleWhileRunning(int number, string visible)
    {
        var seen = DeduplicationHooks.ClaimsSeenWhileRunning(MessageNumbered(number));
        SpecAssert.Check($"message {number}'s claim visible outside its handler's transaction",
            seen switch { null => "never probed", > 0 => "yes", _ => "no" }, visible);
    }
}
