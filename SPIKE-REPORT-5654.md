# Spike report: durable workflows by journal replay on Marten (GH-5654, for #5405)

Branch `gh-5654-workflow-replay-spike`. Nothing here is public API. The engine lives in the test project
(`src/Persistence/MartenTests/Workflows/Engine`) on purpose, so nothing in a shipped package changed.

## Result

All six scenarios pass on Marten (net10.0, 7 tests in `MartenTests.Workflows.durable_workflow_replay_spike`, stable
over three runs). The decided model works as written, with the few adjustments listed under "Where the spike differs
from the decisions".

## What was built

| File | What it is |
|------|-----------|
| `Engine/IWorkflowContext.cs` | `[DurableWorkflow(Name, Version)]`, `IWorkflowContext`, `WorkflowReplayMismatchException` |
| `Engine/WorkflowEvents.cs` | The framework events (`WorkflowStarted`, `WorkflowValueRecorded`, `WorkflowFailureRegistered`, `WorkflowTimedOut`, `WorkflowDelayElapsed`, `WorkflowCompleted`, `WorkflowFaulted`, `SagaCompensating`, `SagaCompensated`), the two framework timer messages (`WorkflowTimeout`, `WorkflowDelay`) and the event header names |
| `Engine/WorkflowState.cs` | The `WorkflowState` snapshot and its Inline `SingleStreamProjection` (an `Evolve` override over every event in the stream) |
| `Engine/WorkflowDefinition.cs` | A definition per method, workflow id rules, a small IL reader, and the registry |
| `Engine/WorkflowReplay.cs` | The replay itself: the `IWorkflowContext` implementation that matches calls against the journal |
| `Engine/WorkflowRunner.cs` | Resume: load, append the arriving message, replay, append and send. Plus `WorkflowCompensation` |
| `Engine/WorkflowHandler.cs` | `WorkflowHandler<T>`: an ordinary generated Wolverine handler closed over every message type a workflow receives; `AddDurableWorkflows(...)` |
| `PlaceOrderWorkflow.cs` | The #5405 example (plus two `OnFailure` registrations and one `RunAsync`), a "changed" copy for the mismatch test, stub handlers |
| `durable_workflow_replay_spike.cs` | The tests |

About 1,200 lines of engine code, comments included. Approach in one paragraph: `AddDurableWorkflows(typeof(PlaceOrderWorkflow))` reads
each `[DurableWorkflow]` method, finds the message types it receives by reading its IL for
`InvokeAsync<T>`/`WaitForAsync<T>` calls, and registers a closed `WorkflowHandler<T>` for each of them plus the
trigger and the two timer messages. That handler is plain Wolverine codegen with `[Transactional]`, so the Marten
session, the outbox, the inbox and the error policies are the normal ones. The handler calls
`WorkflowRunner.ResumeAsync`: `FetchForWriting<WorkflowState>(id)` (snapshot plus stream version),
`FetchStreamAsync` for the journal, add the arriving message to the end, run the method from the top, then append
what the run did and put its sends in the outbox. One commit covers the events, the snapshot and the outbox rows.

### The journal for one completed order

```
WorkflowStarted              name, version, IL fingerprint
place_order                  wf=trigger
WorkflowFailureRegistered    OnFailure(ReleaseInventory), step 0
reserve_inventory            wf=sent, step 1, awaits InventoryReserved
inventory_reserved           wf=received          (found its workflow through the saga-id header)
workflow_timeout             wf=sent, step 2      (the scheduled timeout behind WaitForAsync<PaymentReceived>)
payment_received             wf=received
WorkflowFailureRegistered    OnFailure(RefundPayment), step 3
WorkflowValueRecorded        RunAsync result (the shipping label), step 4
ship_order                   wf=sent, step 5
WorkflowCompleted
```

The messages are stored as themselves; the `wf` event header says whether a message was the trigger, sent, received
or (during compensation) an undo. Matching: sends, recorded values and `OnFailure` registrations by position; received
messages by type in arrival order; a timeout or elapsed delay by the step number that scheduled it.

## Tests

| Test | Scenario |
|------|----------|
| `start_then_the_invoke_reply_resumes_it` | 1. start, then the `InvokeAsync` reply (correlated only by the saga-id header) resumes it |
| `wait_for_is_answered` | 2a. `WaitForAsync` answered; also checks the resume read 6 events and the label I/O ran once |
| `wait_for_times_out` | 2b. timeout via `PlayScheduledMessagesAsync`, `ReleaseInventory` sent; a late payment changes nothing |
| `a_message_that_arrives_before_the_workflow_waits_for_it` | 3. `PaymentReceived` arrives while the workflow still waits on inventory; it is consumed later, with no timeout scheduled |
| `resumes_from_the_stream_after_a_restart` | 4. host stopped while waiting; a new host on the same database resumes from the stream and finishes |
| `a_changed_definition_that_no_longer_matches_the_journal_faults_the_instance` | 5. same name and version, code now sends `ChargeCard` first: `Faulted`, nothing sent, resume message dead-lettered |
| `on_failure_undo_messages_are_sent_in_lifo_order_when_the_workflow_fails_for_good` | 6. `RunAsync` throws on every attempt (1 + 2 retries), then `RefundPayment` then `ReleaseInventory` go out in one outbox transaction |

The fault message from test 5:

> Workflow PlaceOrderWorkflow.Run v1 ({id}) no longer matches its journal: at step 1 the code sends ChargeCard, but
> the journal recorded a send of ReserveInventory (stream version 4). The workflow definition changed under this
> running instance.

Run: `WOLVERINE_POSTGRES="Host=localhost;Port=5442;Database=wf_spike;Username=postgres;password=postgres" dotnet test --project src/Persistence/MartenTests -f net10.0 --filter-class "*durable_workflow_replay_spike*"`

## How many events a resume reads

Every resume reads the snapshot row (`FetchForWriting`, one query) and then the whole stream so far (one query).
For `PlaceOrder`: the inventory reply reads 4 events, the payment reads 6 (asserted in the test), and a completed
instance has 11. The cost per resume grows with the number of steps, so a workflow with N steps reads O(N) events per
resume and O(N^2) over its life. For workflows of tens of steps this is noise next to the transaction itself. For long
loops (an agent loop, a polling loop) it is not, and the real build needs one of: an in-memory journal cache on the
node that owns the instance (the `IAgent` ownership idea from the 2026-11-06 notes) that reads only events after the
cached version; or a Temporal-style "continue as new" that starts a fresh stream.

## What was hard

1. **Suspending without an exception.** A suspended method can't be stopped by throwing, because user `try/catch`
   would swallow it, and the decisions encourage `try/catch` around steps for compensation. The spike leaves the
   method awaiting a task that never completes and lets the garbage collector have it. Consequence: `finally` blocks
   and `using` disposals in a workflow method do not run when it suspends. That needs a doc note and an analyzer rule
   (#5403).
2. **Telling a sent message from a received one of the same type.** Storing the messages themselves means the stream
   needs per-event direction. The spike uses Marten event headers, which means turning on
   `Events.MetadataConfig.HeadersEnabled` for the user's whole store (a schema change). The same headers carry the
   step number and the awaited reply type, which is what lets the snapshot projection show "waiting for X" from events
   alone.
3. **Compensation for registrations the failed run made.** A failing run commits nothing, so an `OnFailure`
   registered in that run (here `RefundPayment`, registered after the payment arrived) is not in the journal. The
   spike carries the failed run's registrations on `Exception.Data` to the compensation step, and falls back to the
   journal's registrations if they're missing (a recovered message after a crash).
4. **Knowing whether the original run waited.** `WaitForAsync` on replay has to give the same answer as the original
   run. The rule that works: if the scheduled `WorkflowTimeout` is the next positional entry, the original run waited,
   and whichever came first (the first unconsumed `T` or the `WorkflowTimedOut` for that step) answers it; if it is
   not, the original run found a `T` already in the journal, so the first unconsumed `T` answers it, and if there is
   none that is a mismatch.
5. **Marten's conventions.** A `Version` property on `WorkflowState` was silently overwritten with the stream version
   (renamed to `WorkflowVersion`).

## Where the spike differs from the decisions

- **"The first not-yet-completed call performs its effect (or suspends) and the method stops there."** The spike
  only stops at waits. A send, `OnFailure`, `Now()`, `NewGuid()` or `RunAsync` in new territory is recorded and the
  method carries on in the same transaction. Stopping after every send would cost a self-message and a transaction per
  step without adding durability, because the event and its outbox row already commit together. `RunAsync` is at
  least once: its result commits with the rest of the resume, so a failure after it re-runs it. Committing after each
  `RunAsync` would shrink that window at the cost of a round trip; that's a decision for the real build.
- **Sends are buffered** and only go to the outbox after the run finishes, so a run that fails or faults sends nothing.
- **Error policy.** The spike sets the workflow chain's policies itself (`ConcurrencyException`: retry with cooldown;
  mismatch: straight to the error queue; anything else: retry twice, then dead-letter and compensate). That overrides
  the user's global policies. The real build should compensate from #5556's "retries used up" hook, so the user's
  policies and `[TerminalFailure]` apply. The dead letter is not tagged `compensated`, and `SagaCompensationFailed`
  is not written.
- **Faulted is terminal in the spike.** It commits `WorkflowFaulted` and throws, so the resume message is
  dead-lettered. Retrying that dead letter after a fix (versioning decision 3) needs the replay to ignore the earlier
  `WorkflowFaulted` and the status to reset; not built.
- **Not built:** `Fault<TRequest>` / `WorkflowStepFailedException`, the `InvokeAsync` timeout, refusing an
  uncorrelatable `WaitForAsync<T>` at startup (the IL reader finds the types, so it is easy), string workflow ids
  (Guid only; the stream identity must match the store), `ISagaHistoryDiagnostics`, the saga catalog entry, tenancy.
- **Event forwarding is not filtered** (see risks).

## Rough edges and risks for the real build

- **Republishing.** Workflow streams are tagged with the `workflow_state` stream type. `PublishEventsToWolverine`
  with no allow-list forwards every event, which would re-send `ReserveInventory` and friends as messages. The fix is
  small: in `EventRelay` and the inline forwarding, skip events whose stream type is a workflow unless the user opts
  in. Not implemented here because it changes shipped code.
- **Duplicates.** A redelivered resume message would be appended twice as received and could answer a later wait.
  The durable inbox in the same Marten transaction prevents that on durable endpoints; storing the envelope id as an
  event header and skipping a repeat would make it independent of the endpoint mode.
- **Message types become event types** in the user's store, under Marten's default aliases (`reserve_inventory`).
  A user event with the same class name in another namespace would collide. A rename needs an alias (as decided).
- **Recorded values and `OnFailure` messages** are stored as System.Text.Json strings with assembly-qualified type
  names (no version). The real build should use the store's serializer and Wolverine's message type names.
- **Parallel `ctx` calls** (`Task.WhenAll` over two `InvokeAsync` calls) are not supported or tested; the positional
  order and the suspension signal assume one call at a time. Add to the #5403 analyzer list.
- **Concurrency** relies on `FetchForWriting`'s version check plus a retry; two messages arriving at once for one
  workflow were not exercised by the tests.
- **The IL reader** finds received types only for direct `ctx` calls in the workflow method, not in helper methods it
  calls. The **fingerprint** hashes raw IL including metadata tokens, so an unrelated change elsewhere in the same
  assembly can change it. It needs token normalization (resolve tokens to member names) before it can drive
  CritterWatch's "definition changed" flag.

### Store-agnostic over #5415, Polecat and Fisher

The runner needs only: `FetchForWriting<T>` with an Inline snapshot, reading a stream's events up to a version,
`BuildEvent` plus per-event headers, `AppendMany`, a stream type tag, and an outbox enlisted in the store's session.
All but the headers are already in the JasperFx.Events abstractions #5415 builds on. Polecat has a `HeadersEnabled`
switch like Marten's; Fisher's docs say headers are kept "where the store keeps them", so per-event headers on Fisher
need checking before this design is committed. If a store can't keep headers, the fallback is a framework wrapper
event for received messages, which goes against "the messages themselves". The projection itself is an `Evolve`
override with no conventional `Apply` methods, so it needs no source generator in any store.

## Does anything argue for the source-generator design?

Not much. The one real argument is the resume cost above: a generated saga would read one snapshot per message
instead of the whole journal. That is better solved with a journal cache on the owning node or continue-as-new than
with a compiler-sized generator that has to split loops, `try/catch` across awaits and locals into states. The other
things a generator would have given us came without it: startup knows the received types by reading IL, the
determinism rule is enforced at runtime by the mismatch check (with the analyzer to come), and the code stays one
readable method that runs as written. Journal replay is the right model to build on.
