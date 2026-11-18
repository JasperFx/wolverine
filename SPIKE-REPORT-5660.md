# Spike report: durable workflows by source generator (GH-5660, for #5405)

Branch `gh-5660-workflow-sourcegen-spike`. Nothing here is public API. The generator is a new project,
`src/Wolverine.SourceGeneration`; the runtime it generates against (`IWorkflowContext`, `WorkflowSaga`,
`WorkflowContext`) lives in the test project under `src/Persistence/MartenTests/Workflows/Runtime`, in the
`Wolverine.Workflows` namespace it would have in Wolverine, so no shipped package changed.

## Result

Every scenario in #5660 passes on Marten (net10.0): 9 integration tests in
`MartenTests.Workflows.durable_workflow_sourcegen_spike` and 24 generator tests in
`MartenTests.Workflows.Generator.durable_workflow_generator_diagnostics`, 33 in all, stable over three runs. The
approach works, and the generated saga reads like something a person would write. The real costs are the
restrictions (no loops around a wait being the big one) and how fragile in-flight instances are when the workflow
method is edited; both are below.

## What was built

| File | What it is |
|------|-----------|
| `src/Wolverine.SourceGeneration/DurableWorkflowGenerator.cs` | The `IIncrementalGenerator`: `ForAttributeWithMetadataName("Wolverine.Workflows.DurableWorkflowAttribute")`, one generated file per workflow method. All work happens in the transform, which returns only strings and plain values, so the pipeline caches on them |
| `WorkflowAnalysis.cs` | Reads the method: the shape rules, where it waits, which locals live across a wait, the workflow id member, and every refusal |
| `SagaEmitter.cs` | Writes the saga: cuts the body at each wait into steps, and gives the code after an `if` that contains a wait a method of its own |
| `WorkflowRewriter.cs` | Copies the user's statements into the saga, changing only hoisted locals (to properties), `return;` (to complete the workflow) and unqualified static members of the workflow class |
| `WorkflowDiagnostics.cs` | WOLV1001 to WOLV1011 |
| `MartenTests/Workflows/Runtime/*` | `[DurableWorkflow]`, `IWorkflowContext`, the `WorkflowSaga` base class, `WorkflowContext` (what the copied code calls), `WorkflowFailed` / `WorkflowFailure` (compensation) |
| `MartenTests/Workflows/PlaceOrderWorkflow.cs` | The #5405 example (with two `OnFailure` registrations and the label I/O through `RunAsync`, as in #5654), and `ReviewOrderWorkflow` for a wait inside an `if` and `DelayAsync` |
| `MartenTests/Workflows/durable_workflow_sourcegen_spike.cs` | The integration tests |
| `MartenTests/Workflows/Generator/*` | `CSharpGeneratorDriver` tests: every refusal, and splits that must compile |

About 1,750 lines of generator and 350 of runtime, comments included. Set up like `Wolverine.Analyzers`:
netstandard2.0 as a one-entry `<TargetFrameworks>`, Roslyn 4.8 so older compilers load it, `Build Project="false"` in
`wolverine.slnx`. MartenTests references it with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`, and the
built dll by path for the generator tests (a second `ProjectReference` is NU1504, the same answer as CoreTests). The
generated files are written to `obj/<config>/<tfm>/generated/` (`EmitCompilerGeneratedFiles`).

I put the generator in its own project rather than in `Wolverine.Analyzers` so the spike is easy to drop. For the real
build, shipping it inside `WolverineFx.Analyzers` is the natural home: that package already reaches every project that
references WolverineFx, and an unused `ForAttributeWithMetadataName` costs nothing.

## How it works

Each `await` on a context member that waits for a message ends a step: `InvokeAsync<T>` sends the request (with the
saga id in the header) and ends; `WaitForAsync<T>(timeout)` schedules a generated `TimeoutMessage` and ends;
`DelayAsync` schedules the same timeout message and ends. The next message runs the next step, so pausing is the end of
a handler and nothing is left waiting. `SendAsync`, `PublishAsync` and `RunAsync` are awaited inside the step; they go
through the outbox in the step's transaction. The saga is an ordinary Marten document saga (`IntegrateWithWolverine`),
retained for 7 days after it completes (#5572) so its final state can be read.

Each `Handle` method switches on the saga's `Step`. A message the workflow isn't waiting for at its current step is
logged and dropped; a message for a finished or unknown workflow goes to a generated `NotFound`.

Failure: every handler catches, puts the saga id and the undo messages registered so far on `Exception.Data` (the
failed attempt's own registrations roll back with it), and rethrows. The generated `Configure(HandlerChain)` retries
the step twice, then dead-letters the message and sends a generated `PlaceOrderWorkflowFailed` to the saga. The saga's
handler for it sends the undo messages newest first and marks itself `Compensated`, so the undo messages and the new
status commit in one transaction. If the first step failed there is no saga yet, and `NotFound` sends them without one.

### PlaceOrder, as written

```csharp
public static class PlaceOrderWorkflow
{
    [DurableWorkflow]
    public static async Task Run(PlaceOrder order, IWorkflowContext ctx, IShippingLabels labels)
    {
        ctx.OnFailure(new ReleaseInventory(order.OrderId));
        var reserved = await ctx.InvokeAsync<InventoryReserved>(new ReserveInventory(order.OrderId));

        var paid = await ctx.WaitForAsync<PaymentReceived>(timeout: 1.Days());
        if (paid is null)
        {
            await ctx.SendAsync(new ReleaseInventory(order.OrderId));
            return;
        }

        ctx.OnFailure(new RefundPayment(order.OrderId, paid.Amount));
        var label = await ctx.RunAsync(() => labels.CreateLabelAsync(order.OrderId, order.Address));
        await ctx.SendAsync(new ShipOrder(order.OrderId, $"{label} from {reserved.Warehouse}"));
    }
}
```

### PlaceOrder, as generated

`MartenTests.Workflows.PlaceOrderWorkflowSaga.g.cs`, unedited, without its header and usings:

```csharp
/// <summary>
/// The durable workflow PlaceOrderWorkflow.Run (version 1) as a saga, generated from
/// PlaceOrderWorkflow.Run() in PlaceOrderWorkflow.cs. Each await that waits for a message ended a step;
/// the saga stores the step it is waiting at, the triggering message and the locals that live across a step.
/// </summary>
[RetainWhenCompleted(Days = 7)]
public partial class PlaceOrderWorkflowSaga : WorkflowSaga
{
    public const string WorkflowName = "PlaceOrderWorkflow.Run";
    public const int WorkflowVersion = 1;

    // The triggering message
    public PlaceOrder Order { get; set; }

    // The locals that live across a step
    public InventoryReserved? Reserved { get; set; }

    // PlaceOrder starts the workflow and runs it up to its first wait
    public async Task Start(PlaceOrder order, IMessageContext messageContext, IShippingLabels labels)
    {
        Id = order.OrderId;
        Order = order;
        var ctx = new WorkflowContext(this, messageContext);
        try
        {
            await begin(ctx, labels);
        }
        catch (Exception e)
        {
            ctx.RecordFailure(e);
            throw;
        }
    }

    // Resumes the workflow at step 1 (InvokeAsync<InventoryReserved>)
    public async Task Handle(InventoryReserved message, IMessageContext messageContext, IShippingLabels labels)
    {
        var ctx = new WorkflowContext(this, messageContext);
        try
        {
            switch (Step)
            {
                case 1:
                    await resumeAtStep1(message, ctx, labels);
                    break;
                default:
                    // Not what the workflow is waiting for now: a duplicate, or a message that came too late
                    ctx.Ignore(message);
                    break;
            }
        }
        catch (Exception e)
        {
            ctx.RecordFailure(e);
            throw;
        }
    }

    // Resumes the workflow at step 2 (WaitForAsync<PaymentReceived>)
    public async Task Handle(PaymentReceived message, IMessageContext messageContext, IShippingLabels labels)
    {
        var ctx = new WorkflowContext(this, messageContext);
        try
        {
            switch (Step)
            {
                case 2:
                    await resumeAtStep2(message, ctx, labels);
                    break;
                default:
                    // Not what the workflow is waiting for now: a duplicate, or a message that came too late
                    ctx.Ignore(message);
                    break;
            }
        }
        catch (Exception e)
        {
            ctx.RecordFailure(e);
            throw;
        }
    }

    // A message for a workflow that has finished, or never started
    public static void NotFound(InventoryReserved message, IMessageContext messageContext) => LogNotFound(messageContext, WorkflowName, message);

    // A message for a workflow that has finished, or never started
    public static void NotFound(PaymentReceived message, IMessageContext messageContext) => LogNotFound(messageContext, WorkflowName, message);

    // A WaitForAsync timed out, or a DelayAsync is over
    public async Task Handle(PlaceOrderWorkflowTimeout timeout, IMessageContext messageContext, IShippingLabels labels)
    {
        var ctx = new WorkflowContext(this, messageContext);
        try
        {
            // A timeout for a step the workflow has already left
            if (timeout.Step != Step)
            {
                ctx.Ignore(timeout);
                return;
            }

            switch (Step)
            {
                case 2:
                    // WaitForAsync<PaymentReceived> timed out
                    await resumeAtStep2(null, ctx, labels);
                    break;
            }
        }
        catch (Exception e)
        {
            ctx.RecordFailure(e);
            throw;
        }
    }

    // The workflow failed for good: send its undo messages, newest first, in this handler's transaction
    public Task Handle(PlaceOrderWorkflowFailed failed, IMessageContext messageContext) => CompensateAsync(failed, messageContext);

    // ... or, when it failed in its first step, without a saga to update
    public static Task NotFound(PlaceOrderWorkflowFailed failed, IMessageContext messageContext) => CompensateWithoutStateAsync(failed, messageContext);

    public static void Configure(HandlerChain chain)
    {
        if (chain.MessageType == typeof(PlaceOrderWorkflowFailed))
        {
            // Sending the undo messages failed: retry, then dead-letter
            chain.OnAnyException().RetryTimes(3);
            return;
        }

        // Two messages for this workflow at once: the loser of the saga's version check runs again
        chain.OnException<global::JasperFx.ConcurrencyException>()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));

        // The workflow threw: retry the step twice, then dead-letter the message and send the undo messages
        chain.OnAnyException().RetryTimes(2)
            .Then.MoveToErrorQueue().And(WorkflowFailure.CompensateAsync<PlaceOrderWorkflowFailed>, "Compensate workflow PlaceOrderWorkflow.Run");
    }

    // The workflow method, step by step

    // PlaceOrderWorkflow.Run from the top, up to its first wait
    private async Task begin(WorkflowContext ctx, IShippingLabels labels)
    {
        ctx.OnFailure(new ReleaseInventory(Order.OrderId));
        // var reserved = await ctx.InvokeAsync<InventoryReserved>(new ReserveInventory(order.OrderId));
        await ctx.SendAsync(new ReserveInventory(Order.OrderId));
        SuspendAt(1, "InventoryReserved");
        return;
    }

    // Step 1 resumes after:
    // var reserved = await ctx.InvokeAsync<InventoryReserved>(new ReserveInventory(order.OrderId));
    // with the InventoryReserved reply
    private async Task resumeAtStep1(InventoryReserved reserved, WorkflowContext ctx, IShippingLabels labels)
    {
        Reserved = reserved;
        // var paid = await ctx.WaitForAsync<PaymentReceived>(timeout: 1.Days());
        await ctx.ScheduleTimeoutAsync(new PlaceOrderWorkflowTimeout(Id, 2, 1.Days()));
        SuspendAt(2, "PaymentReceived");
        return;
    }

    // Step 2 resumes after:
    // var paid = await ctx.WaitForAsync<PaymentReceived>(timeout: 1.Days());
    // with the PaymentReceived, or null when the wait timed out
    private async Task resumeAtStep2(PaymentReceived? paid, WorkflowContext ctx, IShippingLabels labels)
    {
        if (paid is null)
        {
            await ctx.SendAsync(new ReleaseInventory(Order.OrderId));
            CompleteWorkflow();
            return;
        }

        ctx.OnFailure(new RefundPayment(Order.OrderId, paid.Amount));
        var label = await ctx.RunAsync(() => labels.CreateLabelAsync(Order.OrderId, Order.Address));
        await ctx.SendAsync(new ShipOrder(Order.OrderId, $"{label} from {Reserved.Warehouse}"));
        CompleteWorkflow();
    }
}

/// <summary>Resumes PlaceOrderWorkflowSaga when a WaitForAsync times out or a DelayAsync is over</summary>
public record PlaceOrderWorkflowTimeout(Guid Id, int Step, TimeSpan Delay) : TimeoutMessage(Delay);

/// <summary>Tells PlaceOrderWorkflowSaga that its workflow failed for good</summary>
public class PlaceOrderWorkflowFailed : WorkflowFailed
{
}
```

`reserved` lives across the wait for the payment, so it is a property; `paid` and `label` don't, so they stay
locals. For a wait inside an `if`, the code after the `if` moves to a method both branches call; from
`ReviewOrderWorkflow`:

```csharp
    private async Task begin(WorkflowContext ctx)
    {
        Decision = "approved automatically";
        if (Review.Amount > ReviewOrderWorkflow.ApprovalLimit)
        {
            await ctx.SendAsync(new RequestApproval(Review.OrderId, Review.Amount));
            // var approval = await ctx.WaitForAsync<ManagerApproved>(4.Hours());
            await ctx.ScheduleTimeoutAsync(new ReviewOrderWorkflowTimeout(Id, 1, 4.Hours()));
            SuspendAt(1, "ManagerApproved");
            return;
        }
        else
        {
            await continueAfterLine73(ctx);
        }
    }

    private async Task resumeAtStep1(ManagerApproved? approval, WorkflowContext ctx)
    {
        Decision = approval is null ? "rejected, no answer in time" : $"approved by {approval.Manager}";
        await continueAfterLine73(ctx);
    }
```

## Tests

| Test | Scenario (#5660) |
|------|----------|
| `start_then_the_invoke_reply_resumes_it` | 1. start; the `InvokeAsync` reply comes back through the saga-id header alone and runs step 1 |
| `wait_for_is_answered` | 2a. answered; the label I/O ran once; the saga is retained as completed |
| `wait_for_times_out` | 2b. `PlayScheduledMessagesAsync`: `WaitForAsync` returns null, `ReleaseInventory` goes out; a late payment goes to `NotFound` |
| `a_local_assigned_before_a_wait_is_used_after_it` | 3. `reserved` is on the saga while it waits for the payment, and ends up in the label |
| `wait_for_is_answered` / `wait_for_times_out` | 4. `if (paid is null)` both ways |
| `a_wait_inside_an_if_rejoins_the_code_after_the_if`, `an_if_with_a_wait_that_does_not_run_goes_straight_to_the_code_after_it` | 4, the harder case: a wait inside the `if`, a local set in the branch and read after it, and `DelayAsync` |
| `a_message_the_workflow_is_not_waiting_for_is_ignored` | an early payment is dropped (see below) |
| `resumes_after_the_host_restarts` | 5. host stopped while waiting, a new host on the same database finishes it |
| `on_failure_undo_messages_are_sent_newest_first_when_the_workflow_fails_for_good` | 6. three attempts, `PaymentReceived` dead-lettered, then `RefundPayment` (registered by the failing step itself) and `ReleaseInventory`, both sent by the compensation handler; saga `Compensated` with a `CompensationLog` entry |
| `durable_workflow_generator_diagnostics` (24) | 7. every refusal below, plus two runs whose generated code must compile with no errors and no warnings |

Run: `WOLVERINE_POSTGRES="Host=localhost;Port=5442;Database=wf_sourcegen;Username=postgres;password=postgres" dotnet test --project src/Persistence/MartenTests -f net10.0 --filter-class "*durable_workflow*"`

## What was hard to split, and how

1. **An `if` with a wait in one branch.** The code after the `if` has to run at the end of both branches, and one of
   them is in a later message. Copying it into both would double it at every nesting level, so it becomes a method
   (`continueAfterLine73`) that each branch calls as its last act. Every call to a step or join method is a tail
   call, so a `return;` anywhere ends the whole handler. `else if` chains fall out of the same rule.
2. **Which locals live across a wait.** A local is stored on the saga when it is read after a wait that comes after
   its declaration, in source order. Source order is execution order only because loops around a wait and `goto` are
   refused; that is what makes the rule sound. A local assigned by `x = await ctx...` is always stored.
3. **Nullable flow across steps.** After `if (paid is null) return;` the compiler knows `paid` isn't null, but only
   within the method. Once the code after it is in another method, the flow state is gone. The generated file turns
   nullable warnings off; the user's own method is still compiled and checked as written, which is where the
   warnings belong.
4. **The copied code depends on its file.** It uses the file's usings (`1.Days()`), the workflow class's static
   members by short name, and the namespace. The generated file copies the usings and the namespace, qualifies
   statics (`ReviewOrderWorkflow.ApprovalLimit`), and refuses private members, because the saga is another class.
   Making the saga a nested class of a `partial` workflow class would allow private members; I didn't, to avoid
   requiring `partial`.
5. **The shape of the await.** `var x = await ctx...;`, `x = await ctx...;` and `await ctx...;` each give a different
   resume method (a parameter named after the local, an assignment, nothing). An await nested inside an expression
   (`Use(await ctx.InvokeAsync<T>(...))`) is refused rather than spilled into a temporary.
6. **Undo messages registered by the failing step.** Same problem #5654 had: the failing attempt rolls back, including
   its own `OnFailure`. Same answer: they ride on `Exception.Data` to the compensation.

## What the first cut refuses

All errors, and a workflow with an error gets no saga:

- WOLV1001 a non-static method
- WOLV1002 an await on anything but an `IWorkflowContext` member (including `await foreach` and `await using`)
- WOLV1003 a `for` / `foreach` / `while` / `do` loop containing a wait
- WOLV1004 a `try` block, `catch`, `finally`, `using` block, `using` declaration or `lock` containing a wait
- WOLV1005 a wait that isn't a statement of the method itself: nested in an expression, stored and awaited later,
  passed to `Task.WhenAll`, or inside a lambda
- WOLV1006 a `switch` or label around a wait; `goto` and local functions anywhere in a method that waits
- WOLV1007 a pattern, `out`, `const`, `using`, `foreach`, `catch` or multi-variable local that lives across a wait
- WOLV1008 the shape: block body, returns `Task`, not generic, top-level class, trigger first and exactly one
  `IWorkflowContext`, a `Guid` id, not waiting for its own trigger type
- WOLV1009 a local living across a wait whose type can't be stored (best effort: delegates, tasks, disposables,
  `CancellationToken`, `Type`, reflection types, `object`, `dynamic`, interfaces and abstract types outside the
  generic collection interfaces, ref structs, pointers; checked through arrays and type arguments)
- WOLV1010 a private or protected member of the workflow class
- WOLV1011 (warning) `WaitForAsync<T>` where `T` has no member that names the workflow

A loop or `try` with no wait inside it is copied as it is, and a `SendAsync` inside a loop is fine.

## Where the spike differs from the decisions

- **Only the waiting members end a step.** `SendAsync`, `PublishAsync` and `RunAsync` run inside the step. Ending a
  step on a send would cost a message and a transaction and add nothing, since the send already commits with the step.
- **Q3:** `Now()` and `NewGuid()` aren't needed: code between two waits runs once per attempt, not on every replay.
  `RunAsync` is only needed because of WOLV1002. With a generator, an await on anything else could simply run inside
  the step; the rule exists for replay determinism, which no longer applies. I'd relax WOLV1002 to "only the
  context's waiting members end a step" (then `try`/`using` around ordinary awaits is fine too).
- **Early messages are dropped**, logged. #5654 stored a message that arrived before its wait and used it later. The
  generator knows every awaited type, so a typed "arrived early" property per type is a small addition.
- **Compensation hangs off a chain-level error policy** written into the generated `Configure`, which overrides the
  user's global policies, as in #5654. The real build should use #5556's "retries used up" hook. The dead letter
  isn't tagged `compensated`, and `CompensationFailed` isn't set. The send from the error action to the saga is not
  in the same transaction as the dead-lettering.
- **No stream.** The saga document is the whole state; the wrapper-event stream was optional and isn't built.
  `WorkflowSaga` implements the current `ICompensatingSaga` stub (`Status`, `CompensationLog`).
- **Not built:** `Fault<T>` / `WorkflowStepFailedException`, the `InvokeAsync` timeout, versions side by side (a
  version only adds `V2` to the saga name; only the newest version should have `Start`, which needs the generator to
  see every version at once), string ids, tenancy, `ISagaHistoryDiagnostics`.

## Compared with journal replay (#5654)

| | Replay (#5654) | Generated saga (this spike) |
|--|--|--|
| A resume reads | the snapshot plus the whole stream, O(N) per resume | one saga document |
| Pausing | a task left awaiting forever; `finally` / `using` don't run | the handler returns |
| `try` / `using` / loops around a wait | allowed, with the `finally` caveat | refused at compile time |
| Determinism rule | needed, checked at runtime by the mismatch check | not needed |
| Code changed under a running instance | detected: the instance faults with a clear message | **not detected** (see risks) |
| Early messages | stored, used later | dropped |
| What a person can read or take over | one method; the engine is a black box | one method plus a plain saga class |
| Engine size | about 1,200 lines at runtime | about 1,750 lines of generator, 350 of runtime |
| F# | works (reads IL) | doesn't: no source generators for F# |

## Risks for 7.0

1. **Editing a workflow breaks running instances without a word.** Step numbers come from the order of the waits,
   and property names from local names. Insert a wait, and a saga waiting at step 2 resumes in the wrong place; rename
   a local, and its stored value is lost. Replay had a mismatch check for this; a saga has nothing. The cheap fix:
   the generator emits a layout fingerprint (the steps, their awaited types, the stored locals and their types), the
   saga stores it at `Start`, and a resume against a different layout faults the instance. That, plus the side-by-side
   versions of Q5, has to be in the first release.
2. **No loops around a wait** rules out the agent loop and polling loop that #5405 names as a reason to build this.
   Loops can be split (the compiler's own async state machine does it: the loop condition becomes a step and a
   back-jump re-enters it), but the generated code then stops looking like something a person would write, and the
   "read locals in source order" rule needs real data flow analysis.
3. **Debugging.** A breakpoint in the workflow method is never hit, because that method never runs; the generated
   saga is where to break. `#line` directives could map the copied statements back to the user's file, but the
   locals are properties there, so stepping still looks different.
4. **IDE and build.** As with any incremental generator, the diagnostics should show up live in the editor on the
   user's own code and the generated file should be browsable under the analyzer node (not tried in Rider or Visual
   Studio here). The generator took 5 ms of a full MartenTests compile (`ReportAnalyzer`; the
   JasperFx.Events generator took 234 ms). A generator and its runtime types ship separately, so their versions have
   to move together.
5. **AOT.** The generated saga is plain C#, and Wolverine handles it like any saga (pre-generated code under
   `TypeLoadMode.Static`). The spike's undo storage (`Type.GetType` plus reflection `System.Text.Json`) isn't trim
   safe; the generator knows every `OnFailure` message type, so it can emit typed storage instead.
6. **F#** gets nothing: there are no C# source generators in F#. F# users would write the saga by hand, which is at
   least a pattern the generated C# documents.
7. **The original method still compiles and can be called**, and its waits throw `NotSupportedException`. An analyzer
   rule for direct calls would help.
8. **Correlation.** A reply comes back through the saga-id header, but if the reply type has its own `Id` member
   Wolverine's saga id conventions pick that first, and the reply goes to the wrong saga or none. Workflow replies need
   a way to prefer the header.
9. **Retries re-run the whole step**, including `RunAsync` I/O that ran before the failure (at least once), as for any
   handler.
