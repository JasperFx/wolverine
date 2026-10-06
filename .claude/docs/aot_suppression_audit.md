# AOT suppression audit (GH-4810)

**Measured 2026-10-04 against `main` at `e91c4a4d1`.** This is the measurement GH-4810 asked for before
any gate gets built:

> take the suppressions in `Wolverine` and the five relational stores, classify each one policy-time vs
> codegen-time by the #4765 rule, and count how many are wrong today. If the answer is "a handful", the
> cheap suppression-audit test is enough. If it is "many", the analyzer earns its keep.

**The answer is "many."** Of 284 `[UnconditionalSuppressMessage]` attributes in scope, 89 justify
themselves with a reachability claim, and roughly **62 of those 89 are false** — the member they sit on
does run in a native image. Across 26 files in 6 assemblies.

## The rule being applied

From GH-4765, measured rather than reasoned (see `reference_only_a_policy_placed_frame_needs_an_aot_root`):

> A reflectively-closed frame needs an AOT root **only** if a **policy** places it. A frame built during
> code generation is both unreachable in a native image and uncollectable by the `IAotRootSource` hook.

The dividing line is which side of this boundary the enclosing member is reached from:

| Side | Entry points | Runs in a native image? |
|---|---|---|
| **Codegen** | `ICodeFile.AssembleTypes` → `Chain.DetermineFrames`, `Frame.GenerateCode`, `IVariableSource.Create`, `Frame.FindVariables`, deferred `UseReturnAction` lambdas | **No.** `StaticTypeLoader.Initialize` calls only `AttachTypesSynchronously` and never `AssembleTypes`. |
| **Policy** | `IHandlerPolicy.Apply`, `IChainPolicy.Apply`, `IEndpointPolicy.Apply`, `ModifyChainAttribute.Modify`, `WolverineParameterAttribute.Modify`, `IPersistenceFrameProvider.ApplyTransactionSupport`, `Endpoint.Compile()` | **Yes.** `HandlerChain.cs:316-332` — `ICodeFile.AttachTypesSynchronously` calls `applyCustomizations`, so the Static attach path *is* the policy pass. |

A justification saying "codegen time only, so this never fires in a native image" is therefore **true on
the codegen side and false on the policy side**, and the two are frequently the same method reached from
two different callers. `SagaChain.cs` is the canonical near-miss: it calls `ApplyTransactionSupport` from
`DetermineFrames`, which is codegen-only, while `AutoApplyTransactions` calls the identical method from a
policy. Same method, opposite verdict, depending on the caller.

## Why the existing analyzers caught none of it

This is the crux, and it is why GH-4810 is a research task rather than a configuration change.

1. **The hazardous sites are deliberately suppressed**, and the suppression hides the true and the false
   cases identically. The analyzer cannot tell a policy-reached call from a codegen-reached one.
2. **ILC reports only an assembly-level rollup for dependencies** — `warning IL2104: Assembly 'Fisher'
   produced trim warnings`, naming no member. The runtime exception then advises inspecting warnings that
   point nowhere near the cause.
3. **The failures that matter are not warnings at all.** `[DynamicDependency]` over a value-type
   instantiation emits no diagnostic: the attribute is accepted, the root is emitted correctly, and ILC
   simply does not generate the code. A generic *virtual* method is the same — no warning, a hard
   `FailFast`.

## Scope and counts

Eight projects, all with trim analysis **on** (`IsAotCompatible=true`, which implies `EnableTrimAnalyzer`
+ `EnableAotAnalyzer`, and `Directory.Build.props` sets `TreatWarningsAsErrors`). There is no "suppression
in a project without analysis" category here — every one is live.

| Project | Suppressions |
|---|---|
| `src/Wolverine` | 202 |
| `Wolverine.EntityFrameworkCore` | 51 |
| `Wolverine.Postgresql` | 9 |
| `Wolverine.SqlServer` | 6 |
| `Wolverine.Sqlite` | 5 |
| `Wolverine.MySql` | 5 |
| `Wolverine.Oracle` | 4 |
| `Wolverine.RDBMS` | 2 |
| **Total** | **284** |

By rule: IL3050 ×86, IL2026 ×82, IL2070 ×28, IL2072 ×21, IL2067 ×20, IL2075 ×18, IL2060 ×5, then a tail
of single digits.

Not counted above but the same bug class, worth a second pass: `Wolverine.Marten/GlobalSuppressions.cs`
(14), `Wolverine.Polecat` (10), `Wolverine.Fisher` (10), `Wolverine.RavenDb` (11), `Wolverine.CosmosDb`.

## The classification

89 suppressions make a reachability claim. Grouped by cluster, with the caller that decides the verdict:

### POLICY-REACHABLE — the justification is false (~62)

| Cluster | Count | Reached from | Claim made |
|---|---|---|---|
| `AggregateHandling.cs` | 14 | `WriteModelAttribute.cs:183`, inside `Modify` | "this is the dynamic codegen path" |
| `HandlerChain.cs` | 7 | `AttachTypesSynchronously` (`:316`) and `HandlerGraph.cs:305` | "AOT consumers run pre-generated handlers via `TypeLoadMode.Static` so the reflective close never fires" |
| `EFCorePersistenceFrameProvider.cs` + its 4 frames | 8 | `ApplyTransactionSupport`, and `AllAttribute` / `QueryableAttribute` / `FromQuerySpecificationAttribute` `Modify` | "Dynamic-mode codegen path" |
| `WriteModelAttribute` / `DcbModelAttribute` / `FromQuerySpecificationAttribute` / `ReadModelAttribute` / `DeciderFunctionAttribute` | 14 | their own `Modify` overrides | "at codegen time" |
| The 4 store `SagaSchemaFor` sites (SqlServer, Postgresql, Sqlite, MySql) | 4 | `LightweightSagaPersistenceFrameProvider.cs:53`, inside `ApplyTransactionSupport` (`:31`) | "Called at codegen time under the JIT, so it does not execute in a native image at all" |
| `LightweightSagaPersistenceFrameProvider.cs` | 3 | `ApplyTransactionSupport`, `CanPersist` | "during Dynamic codegen" |
| `Chain.cs` | 3 | the `HandlerChain` constructor, and `applyCustomizations` | "member walk fires at codegen time only" |
| `ISideEffect.cs` + `EfCoreOpFrames.cs` | 5 | `SideEffectPolicy.Apply` | "the closure fires only at codegen time" |
| `ApplyAncillaryStoreFrame.cs`, `FlushOutgoingMessages.cs`, `HandlerCall.cs`, `DeclarativeLoadDependencies.cs`, `InMemoryPersistenceFrameProvider.cs:56` | 5 | `ApplyTransactionSupport`, `EntityAttribute.Modify`, startup inbox routing | various |

Two of these are contradicted by prose **in the same file**, which is the signature of a defect class that
regenerates faster than review catches it:

- `ApplyAncillaryStoreFrame.cs:7-8` — *"AOT consumers pre-generate via `TypeLoadMode.Static` so the
  reflective close never fires"* — sitting eleven lines above its own XML doc: *"That still happens at
  startup under `TypeLoadMode.Static`, generated code or not."*
- `HandlerChain.cs:314` — *"AOT consumers run pre-generated handlers via `TypeLoadMode.Static` so the
  reflective close never fires"* — decorating `ICodeFile.AttachTypesSynchronously`, which **is** the
  `TypeLoadMode.Static` path.

### CODEGEN-ONLY — the justification is correct (~22)

`SagaStorageVariableSource` and `LoggerVariableSource` (`IVariableSource.Create`); `LoadSagaOperation` and
`SagaOperation` (`FindVariables`); `EventCaptureFrames` and `BoundaryEventCaptureFrames`
(`IReturnVariableAction.Frames()`); `InMemoryPersistenceFrameProvider:74,103,134,136` (deferred
`UseReturnAction` lambdas and `SagaChain.DetermineFrames`); `HandlerDiscovery:181` and
`HandlerRegistry:208` (both guarded by `DynamicCodeBuilder.WithinCodegenCommand`);
`EnrollAndFetchSagaStorageFrame:39` (policy-reachable and correctly says so, citing its own root);
`HandlerGraph.PreBuiltTypes.cs:108`; `RegisterEventsFrame:46`.

### UNCLEAR (~11)

Members that genuinely do run in a native image, but whose justification is a **rooting** claim ("known by
construction", "the constructors are emitted by codegen and preserved") rather than an unreachability
claim — sloppy phrasing, where "at codegen time" means "when the type became known". A naive gate would
flag these; a well-tuned one should not. `HandlerDiscovery:306,309`, `HandlerRegistry:226`,
`HandlerChain:308,310,312,624`, `EfCoreStorageActionApplier:19,21`.

Also dead rather than unclear: `BatchedLoadEntityFrame:24` is constructed only from tests, and
`ConnectionSource.cs:42`'s `ConnectionFrame<T>` has no construction site anywhere in `src/`.

## The asymmetry worth staring at

**~62 false "never runs in a native image" justifications, against 3 production `IAotRootSource`
implementations** (`ApplyAncillaryStoreFrame<T>`, `EnrollAndFetchSagaStorageFrame`,
`CreateTenantedDbContext<T>`). If even a fraction of the 62 are genuinely missing roots rather than merely
mis-worded, the hook is under-populated by an order of magnitude.

That said: a false justification is **not** the same as a live bug. Most of the 62 are reached from policy
*and* happen to be safe, either because the instantiation is over a reference type (canonical sharing
carries it) or because generated code names the closed type statically. The measurement says the *stated
reason* is wrong, which is what made this class invisible — not that there are 62 crashes waiting.

## Recommendation

**Build the analyzer, not the suppression-audit test** — the measurement is what decides this, per the
issue's own gate. At 62 wrong out of 89, a baseline-and-ratchet test would be freezing a mostly-wrong
corpus as "known good" and would still not tell anyone which entries are dangerous.

Ordered by expected value per unit of effort:

1. **A Roslyn analyzer encoding the policy-vs-codegen rule.** The reachability test is mechanical:
   `ICodeFile.AssembleTypes` on one side versus `IHandlerPolicy` / `ModifyChainAttribute` /
   `IPersistenceFrameProvider.ApplyTransactionSupport` on the other. Flagging a
   `CloseAndBuildAs`/`MakeGenericType` reachable from the policy side **without** a corresponding
   `IAotRootSource` contribution would have caught #4803 and #4805 blocker 2 at compile time. Cost: a new
   analyzer project to ship and maintain.

2. **A "root was emitted but not generated" check.** For each type named in the emitted rooting block,
   assert it survives in a published native image. This is the only candidate that could have caught
   #4805 blocker 1a, because that failure produces no diagnostic at any earlier stage. Needs a published
   image, so it is a smoke refinement rather than a static gate — but a *generic* one, driven by the
   rooting block instead of by hand-written scenarios.

3. **Give `CIAotSmoke` teeth.** Unrelated to static analysis, and nearly free: the target is
   `.ProceedAfterFailure()`, so every lane in it — including the store-backed one added in #4809 — reports
   red without blocking a merge.

4. **Correct the ~62 justifications.** Mechanical, near-zero-risk (comment and attribute strings only),
   and it stops the code asserting something untrue. Worth doing whether or not an analyzer lands, but it
   buys no enforcement on its own.

Prefer de-genericizing over rooting wherever the generic buys only a typed delegate. GH-4805 did it for
the saga frame and GH-4811 for the partitioning and deduplication accessors; in both cases the hazard was
deleted rather than worked around. GH-4848 did it for `MessageRouter<T>` / `EmptyMessageRouter<T>`,
which had been the canonical "rooted per message type" example since GH-4287: the routers take the message
`Type` as an argument now, the per-message router roots and the `_frameworkRouterFactories` table are gone,
and the suppressions on `RoutingFor` / `PrepopulateRoutingCache` were deleted rather than reworded. The
value-type case (a handler returning `Guid`) was the one no root could ever have reached.

## Reproducing the counts

```bash
rg -n -U --glob '*.cs' '\[UnconditionalSuppressMessage\((?s:.*?)\)\]' \
  src/Wolverine src/Persistence/Wolverine.RDBMS src/Persistence/Wolverine.SqlServer \
  src/Persistence/Wolverine.Postgresql src/Persistence/Wolverine.Sqlite \
  src/Persistence/MySql/Wolverine.MySql src/Persistence/Oracle/Wolverine.Oracle \
  src/Persistence/Wolverine.EntityFrameworkCore
```

The 284/89 split came from a bracket-balancing walk over those roots (gather lines from each
`UnconditionalSuppressMessage` hit until paren depth returns to zero, then match the justification against
the phrases *codegen time*, *never fires*, *does not execute in a native image*, *pre-generated frames*,
*TypeLoadMode.Static*, *dynamic codegen*). The policy-vs-codegen verdict per cluster was traced by hand;
the largest clusters (`AggregateHandling`, `HandlerChain`, the four store `SagaSchemaFor` sites,
`ApplyAncillaryStoreFrame`, `LightweightSagaPersistenceFrameProvider`) were re-verified directly against
their callers.

## The rules these bugs reduced to

All measured in real native images with `Generating native code` asserted — every one of them passes under
the JIT, so a CoreCLR probe proves nothing.

- A reflectively-closed frame needs an AOT root **only** if a policy places it; a frame built during code
  generation is both unreachable in a native image and uncollectable by the hook.
- `[DynamicDependency]` preserves **metadata**, not **code** — a root over a value-type instantiation is
  silently ineffective. The roots that appear to work (`MessageRouter<T>`, `Applier<T>`) survive via
  reference-type canonical sharing.
- A generic **virtual** method cannot be rooted at all; the fix is a factory supplied by generated code. A
  generic method on a *non-generic interface* is fine.
- ILC substitutes `RuntimeFeature.IsDynamicCodeSupported` with `false` and removes the guarded branch, and
  the IL3050 analyzer honours the same guard — so guarding a reflective close with it both fixes the
  native image and lets the suppression be deleted rather than reworded (GH-4811).
