# Discoveries: Process Manager via Existing Wolverine + Marten Features

This log records what the implementation of `ProcessManagerSample` taught us beyond the initial research notes. It is the feedback loop for the recipe in `docs/guide/durability/marten/process-manager-via-handlers.md` and any future `ProcessManager<TState>` framework proposal.

Entries are grouped by kind: confirmed expectations, corrected expectations, new gotchas, recipe adjustments, and open questions.

## Confirmed expectations

1. **`public Guid Id { get; set; }` is required on the state type.** Without it, `ResetAllMartenDataAsync()` / `CleanAllDataAsync()` throws `InvalidDocumentException` because Marten registers the aggregate as a document type.
2. **Correlation by convention works with no attribute.** A command/event property named `{AggregateTypeName}Id` (for us, `OrderFulfillmentStateId`) resolves the stream id automatically. This held across every continue handler in the sample with no per-handler override.
3. **Inline snapshot projection is enough.** Registering `opts.Projections.Snapshot<OrderFulfillmentState>(SnapshotLifecycle.Inline)` makes the next `FetchForWriting` see the latest state without running a daemon. Between two back-to-back `InvokeMessageAndWaitAsync` calls, the second call sees the first call's effects.
4. **`InvokeMessageAndWaitAsync` commits before returning.** The next `LightweightSession().Events.FetchStreamAsync(id)` on the same thread sees the appended event without any sleep or poll.
5. **`FetchForWriting` on a non-existent stream does return `Aggregate == null`.** This matches the research notes verbatim. However, see the corrected expectation below for why this is not actually reachable from the start handler by default.
6. **Pure-function handler tests are as simple as advertised.** `new OrderFulfillmentState { ... }` constructed directly, passed with a plain event into a static `Handle`, assertions run on the returned `Events` list. No host, no Marten, no DI, no async.
7. **Idempotency via per-step flag check works.** Checking `if (state.ThisStepAlreadyHappened) return new Events();` inside each continue handler makes a duplicate integration event a no-op, reliably, because the inline projection has already reflected the first occurrence by the time the duplicate is dispatched.

## Corrected expectations

1. **`[AggregateHandler]` is not suitable for the "start a new stream" case out of the box.** The research notes suggested a uniform `[AggregateHandler]` pattern for all handlers in the process. In practice, `AggregateHandlerAttribute.OnMissing` defaults to `OnMissing.Simple404` (`AggregateHandlerAttribute.cs:144`). When the aggregate does not yet exist, the handler body is short-circuited: zero events are appended and no exception is thrown, which makes the failure silent and easy to miss.

   The idiomatic fix matches what the Wolverine test suite already does for start cases: a plain handler that returns `IStartStream` via `MartenOps.StartStream<TState>(id, events...)`. See `src/Persistence/MartenTests/AggregateHandlerWorkflow/aggregate_handler_workflow_with_ievent.cs:144` for the reference pattern.

   Consequence for the recipe: the start handler has a different shape from continue handlers. The continue handlers stay on `[AggregateHandler]` because the stream exists by the time they run.

## New gotchas

1. **Silent no-op on missing aggregate.** Tied to the corrected expectation above. If you accidentally put `[AggregateHandler]` on a would-be start handler, the integration test will not throw; it will just report zero events on the stream. First-time failure mode is confusing. The doc should call this out explicitly.
2. **Nullable single-event returns are unsafe.** Returning `TEvent?` (for example `OrderFulfillmentCancelled?`) from an `[AggregateHandler]` handler looks ergonomic but is a trap: `EventCaptureActionSource` generates an unconditional `stream.AppendOne(variable)` with no null check (`AggregateHandlerAttribute.cs:225`). A `return null` would call `AppendOne(null)`. Always return `Events` (possibly empty) for no-op paths. This is the idiom the recipe should teach for any "sometimes no event" shape.
3. **Completion guard and idempotency guard are two different checks.** `if (state.IsTerminal) return;` handles "the process is closed." `if (state.ThisStepAlreadyHappened) return;` handles "at-least-once redelivery of a message that already landed." Merging them would lose the ability to tell a post-terminal late delivery from a normal retry, and both occur in practice. Continue handlers therefore carry two guard lines at the top, not one.

## Recipe adjustments

1. **Step "Write your handlers" needs to split into two sub-steps.** One for the start handler (plain, returns `IStartStream`), one for the continue handlers (`[AggregateHandler]`, returns events / `Events` / `OutgoingMessages`). Treating them uniformly is the trap.
2. **Recommend `Events` as the default continue-handler return shape.** Single-event returns work for the happy path but force you into nullable workarounds on the no-op paths. `Events` handles both cases with the same return type (empty list for no-op, one item for a record, two items when the event also trips completion).
3. **The completion guard section in the Recipe should be two sub-sections, not one.** First sub-section: the terminal-state guard (`IsTerminal`). Second sub-section: the step-already-happened idempotency guard. They solve different problems and readers will conflate them if we don't separate them.

## Open questions

1. Can we configure `[AggregateHandler]` with `OnMissing.ProceedAnyway` (or equivalent) to support a uniform pattern, and should the doc show that as an option? Phase 3 did not surface a need for continue handlers to override this, so the question stays specific to start handlers. Deferred.
2. Does `MartenOps.StartStream<T>` play cleanly with `OutgoingMessages` cascading (needed when the start handler has to schedule a payment timeout)? To be validated in Phase 5.
3. What happens if `StartOrderFulfillment` is dispatched twice for the same id? Expected: `MartenOps.StartStream` throws a "stream already exists" error. Worth confirming and possibly addressing in the doc (either by recommending an idempotent start wrapper or by documenting the expected exception).
4. Concurrency check firing: the sample does not simulate two handlers racing on the same stream. Optimistic concurrency is documented to fire, but the sample does not demonstrate it. Consider adding a dedicated test in Phase 7 polish if time permits.
