# Discoveries: Process Manager via Existing Wolverine + Marten Features

This log records what the implementation of `ProcessManagerSample` taught us beyond the initial research notes. It is the feedback loop for the recipe in `docs/guide/durability/marten/process-manager-via-handlers.md` and any future `ProcessManager<TState>` framework proposal.

Entries are grouped by kind: confirmed expectations, corrected expectations, new gotchas, recipe adjustments, and open questions.

## Confirmed expectations

1. **`public Guid Id { get; set; }` is required on the state type.** Without it, `ResetAllMartenDataAsync()` / `CleanAllDataAsync()` throws `InvalidDocumentException` because Marten registers the aggregate as a document type.
2. **Correlation by convention works with no attribute.** A command property named `{AggregateTypeName}Id` (for us, `OrderFulfillmentStateId`) resolves the stream id automatically.
3. **Inline snapshot projection is enough.** Registering `opts.Projections.Snapshot<OrderFulfillmentState>(SnapshotLifecycle.Inline)` makes the next `FetchForWriting` see the latest state without running a daemon.
4. **`InvokeMessageAndWaitAsync` commits before returning.** The next `LightweightSession().Events.FetchStreamAsync(id)` on the same thread sees the appended event without any sleep or poll.
5. **`FetchForWriting` on a non-existent stream does return `Aggregate == null`.** This matches the research notes verbatim. However, see the corrected expectation below for why this is not actually reachable from the start handler by default.

## Corrected expectations

1. **`[AggregateHandler]` is not suitable for the "start a new stream" case out of the box.** The research notes suggested a uniform `[AggregateHandler]` pattern for all handlers in the process. In practice, `AggregateHandlerAttribute.OnMissing` defaults to `OnMissing.Simple404` (`AggregateHandlerAttribute.cs:144`). When the aggregate does not yet exist, the handler body is short-circuited: zero events are appended and no exception is thrown, which makes the failure silent and easy to miss.

   The idiomatic fix matches what the Wolverine test suite already does for start cases: a plain handler that returns `IStartStream` via `MartenOps.StartStream<TState>(id, events...)`. See `src/Persistence/MartenTests/AggregateHandlerWorkflow/aggregate_handler_workflow_with_ievent.cs:144` for the reference pattern.

   Consequence for the recipe: the start handler has a different shape from continue handlers. The continue handlers stay on `[AggregateHandler]` because the stream exists by the time they run.

## New gotchas

1. **Silent no-op on missing aggregate.** Tied to the corrected expectation above. If you accidentally put `[AggregateHandler]` on a would-be start handler, the integration test will not throw; it will just report zero events on the stream. First-time failure mode is confusing. The doc should call this out explicitly.

## Recipe adjustments

1. **Step "Write your handlers" needs to split into two sub-steps.** One for the start handler (plain, returns `IStartStream`), one for the continue handlers (`[AggregateHandler]`, returns events / `Events` / `OutgoingMessages`). Treating them uniformly is the trap.

## Open questions

1. Can we configure `[AggregateHandler]` with `OnMissing.ProceedAnyway` (or equivalent) to support a uniform pattern, and should the doc show that as an option? Deferred until Phase 3 exposes whether the continue handlers ever want similar overrides (for example, a late-arriving message after a terminal event).
2. Does `MartenOps.StartStream<T>` play cleanly with `OutgoingMessages` cascading (needed when the start handler has to schedule a payment timeout)? To be validated in Phase 5.
