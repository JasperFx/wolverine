# Title

Wolverine Tier 2 cold-start: snapshot extension for the JasperFx codegen pipeline

## TL;DR

Issue [#2728](https://github.com/JasperFx/wolverine/issues/2728) is a multi-PR roadmap to extend the existing JasperFx codegen pipeline so it writes down more than just `MessageHandler` / `HttpHandler` / gRPC adapter bodies — also the post-boot-stable maps that today are rebuilt from scratch on every host startup. This chip is the design pass that grounds the issue's "Medium-confidence" artifact list against the actual call sites in `main`, identifies the JasperFx API surface this work needs, and lays out a sequencing that respects the foundation re-align timing.

Status: **design only**. Implementation PRs depend on (a) the foundation re-align landing so we're building against the stable JasperFx 2.0 codegen surface (currently alpha.8 in Wolverine, alpha.13+ on the target matrix), and (b) the benchmark harness in CritterStackScalability per step 2 of the issue's sequencing.

## Prompt

**Repo: Wolverine** (https://github.com/JasperFx/wolverine). Root: `/Users/jeremymiller/code/wolverine`. Base branch: `main` (currently at `ffc193613` after PR #2840 closed #2838 Kafka fix). All implementation PRs target `main`.

The issue [#2728](https://github.com/JasperFx/wolverine/issues/2728) is the framing document; this chip is the implementation map. Treat the issue's artifact list as the spec; this chip is where the artifact contracts get pinned to actual code paths and where the trust-gate test matrix gets concrete.

If your worktree is rooted anywhere other than the Wolverine repo, STOP and report — do not attempt cross-repo edits.

---

## Surface area — ground-truthed against `main`

The issue's artifact list pointed at line numbers that I verified against `main` today. Drift notes for the implementer:

### Message type-name → `Type` map

- **Source**: `src/Wolverine/Util/WolverineMessageNaming.cs:140` — `static ImHashMap<Type, string> _typeNames`.
- **Lazy-mutation site**: `ToMessageTypeName(Type)` at line 201 — `_typeNames.AddOrUpdate(type, name)` on first call.
- **Pre-population today**: `PrepopulateCache(IEnumerable<Type>)` at line 214 is already invoked at bootstrap (called from `WolverineRuntime.HostService.StartAsync` per the existing #1577 cold-start pass).
- **Snapshot value**: high. The first-call mutation walks 6 `IMessageTypeNaming` strategies (attribute reads, interface walks, generic-type pretty-printing) per type. Snapshot replaces the rebuild with a frozen lookup.
- **Drift from issue text**: none. Issue references `_typeNames` ImHashMap + `PrepopulateCache` at lines 128, 202 — current is 140, 214 (cosmetic shift).

### Handler graph index

- **Sources**:
  - `src/Wolverine/Runtime/Handlers/HandlerGraph.cs:41` — `ImHashMap<Type, HandlerChain> _chains`
  - line 43 — `ImHashMap<Type, IMessageHandler?> _handlers`
  - line 51 — `ImHashMap<string, Type> _messageTypes` (the inverse of message-naming, type-name → Type)
  - line 53 — `ImmutableList<Type> _replyTypes`
- **Lazy-mutation site**: `HandlerFor(messageType, endpoint)` at line 148 — `_handlers.AddOrUpdate(messageType, handler)` for `IAgentCommand` derivations. The issue's "line 161" reference shifted to **line 148** on current `main`.
- **Snapshot value**: high. Eliminates per-startup `Compile()` reconstruction and the `IAgentCommand` lazy fill.
- **Special case**: `_messageTypes` is a snapshot candidate on its own — multiple callers resolve `string → Type` for inbound envelope dispatch.

### Endpoint → serializer cache

- **Source**: `src/Wolverine/Configuration/Endpoint.cs:185` — `ImHashMap<string, IMessageSerializer> _serializers`.
- **Pre-population today**: `Compile()` at lines 510-523 already pre-populates from `runtime.Options.ToSerializerDictionary()`.
- **Remaining miss-path**: `TryFindSerializer(contentType)` at line 598 falls back to `Runtime?.Options.TryFindSerializer(contentType)` — pure read, **no longer a hot-path write**. The issue's "first-miss write at line 578" reflects the pre-#2724 shape.
- **Snapshot value**: low-to-medium. The hot path is already pure reads post-#2724. Snapshot value is moving the `Compile()` rebuild from "for each endpoint per boot" to "load frozen lookup at boot". Useful, but not the dispatch-impacting win the issue implies.

### Routing table

- **Source**: `src/Wolverine/Runtime/WolverineRuntime.Routing.cs:158` — `RoutingFor(Type messageType)`.
- **Cache**: `_messageTypeRouting` (ImHashMap, populated lazily on first call).
- **Lazy-mutation site**: line 185 — `_messageTypeRouting.AddOrUpdate(messageType, router)` on first call per message type.
- **Pre-existing comment** (lines 140-148) is highly relevant:
  > "AOT-clean apps in TypeLoadMode.Static pre-populate this cache at bootstrap (envelope-mapper / message-type discovery sources, see #2715 and the AOT publishing guide) so the steady-state hot path is pure lookups and the miss path's reflective close never fires."
- **Snapshot value**: high. The miss path closes `MessageRouter<>` / `EmptyMessageRouter<>` via reflection, fires the CritterWatch `Observer.MessageRouted` hook, and runs `findRoutes(messageType)` — the latter is where `IMessageRoutingConvention`s, tenant-aware routing, and modular extensions all participate.
- **Highest-risk artifact** per the issue. The snapshot here has to be byte-exact equivalent to the live `findRoutes` result. Trust-gate suite is the gating concern.
- **Composition with #2715**: the AOT pre-population already exists for AOT consumers. Snapshot path subsumes / extends it.

### Pre-warmed cascade-method cache

- **Source**: `src/Wolverine/Runtime/MessageContext.cs:725` — `static ImHashMap<Type, MethodInfo?> _typedEnumerableCascadeMethods`.
- **Lazy-mutation site**: line 764 — `_typedEnumerableCascadeMethods.AddOrUpdate(messageType, method)` on first cascade per closed generic.
- **Pre-population today**: a walker at line 799 already exists (`PrepopulateCache`-style) but is opt-in.
- **Snapshot value**: low at the artifact level (it's a `MethodInfo` cache, doesn't serialize cleanly), but **high at the prewarm-hint level**: the snapshot can record the *list of registered cascading-return types* so the live `PrepopulateCache` walker runs against the known set at boot without scanning.
- **Recommendation**: don't serialize `MethodInfo`. Snapshot the input set (registered cascading types), call the existing prepopulate walker at boot.

### Configuration fingerprint (new)

Per the issue: SHA-256 over the inputs that determine whether the snapshot is still valid. Concrete proposal:

| Input | Source | Hashing approach |
|---|---|---|
| Registered handler types | `HandlerGraph.Discovery.Assemblies` + discovered types | Sort by full-name, hash the FQNs |
| Registered message types | `HandlerGraph._messageTypes` keys + `MappedGenericMessageTypes` | Sort, hash FQNs |
| Endpoint URIs | `WolverineOptions.Transports.AllEndpoints().Select(e => e.Uri)` | Sort, hash URI strings |
| Serializer registrations | `runtime.Options.ToSerializerDictionary()` keys + impl FQNs | Sort, hash `contentType:typeFQN` pairs |
| Routing convention list | `WolverineOptions.RoutingConventions` impl FQNs + relevant config | Sort, hash FQNs (extension authors with non-deterministic config become a known limitation) |
| JasperFx version | `typeof(GeneratedAssembly).Assembly.GetName().Version` | Hash version string |
| Wolverine version | `typeof(WolverineOptions).Assembly.GetName().Version` | Hash version string |
| Codegen rules | `WolverineOptions.CodeGeneration` properties (TypeLoadMode etc.) | Hash relevant property values |

**Stored alongside**: `Internal/Generated/Wolverine/snapshot.fingerprint` (single file, hex SHA-256 + manifest of contributing inputs for debuggability).

**Verified at boot**: compute current fingerprint, compare to stored. Mismatch → log warning, regenerate, continue. Match → load snapshot.

---

## Artifact format options

Two viable approaches; chip recommends option B.

### Option A — JSON sidecars

`Internal/Generated/Wolverine/{snapshot.fingerprint,message-types.json,handlers.json,routes.json,...}` written by codegen, loaded at boot via `System.Text.Json` source-generated readers.

- **Pro**: format is debuggable. Diffing two snapshots is trivial. Source generator (optional applicator) is unnecessary because JSON loads as plain data.
- **Con**: I/O overhead at boot. Cold-start gain partially offset by JSON parse time on first read.
- **Con**: `Type` references need to round-trip via assembly-qualified-name; some types (closed generics from extension assemblies) become fragile under trimming.

### Option B (recommended) — generated partial-class registrations

Codegen emits a partial class (e.g. `WolverineGeneratedSnapshot`) with `static readonly FrozenDictionary<...>` fields initialized at class-init time using `typeof(SomeMessage)` references. Boot path calls a single `WolverineGeneratedSnapshot.Apply(WolverineOptions)` to install the maps.

- **Pro**: zero I/O cost. Static initialization is essentially free.
- **Pro**: trim-friendly. `typeof(SomeMessage)` references keep the types in the trim graph automatically.
- **Pro**: no source generator needed for the runtime path. The optional source-generator applicator (step 7 in the issue) becomes purely "discover the generated partial class via assembly scan" rather than "load and parse data files".
- **Con**: format is less debuggable — but `codegen preview` already exists.
- **Con**: requires the registered types to be public from the perspective of the generated file. Already true for handler types, message types, endpoint types.

**Recommendation**: option B for all dispatch-impacting artifacts (message-naming, handler graph, routing table). Option A acceptable for the fingerprint manifest itself (it's human-debug-only).

---

## Trust-gate test matrix

The issue calls out three test categories. Concrete per-artifact list:

### Per dispatch-impacting artifact (handler graph, routing table)

| Test | What it asserts | Where to put it |
|---|---|---|
| **Round-trip** | Build snapshot → restart host → live-rebuilt map equals snapshot-loaded map | `Testing/CoreTests/CodeGeneration/Snapshot/` (new) |
| **Mutation: handler add/remove** | Register a new handler type → fingerprint rejects old snapshot → new snapshot installed → handler dispatches | Same |
| **Mutation: routing convention** | Add an `IMessageRoutingConvention` → fingerprint rejects → routes reflect convention | Same |
| **Mutation: serializer swap** | Replace default serializer → fingerprint rejects → endpoint serializers updated | Same |
| **Composition: `WolverineExtension`** | Extension that registers handlers / conventions → snapshot built with extension matches live | Same |
| **Composition: multi-tenant routing** | Tenant-aware routing convention → per-tenant routes match live | `Persistence/MartenTests/Snapshot/` (multi-tenant test infra lives here) |

### Per fingerprint

| Test | What it asserts |
|---|---|
| **Deterministic** | Same `WolverineOptions` → same fingerprint across two builds |
| **Order-independent** | Reordering handler-discovery includes does not change fingerprint |
| **Version-sensitive** | Bumping `typeof(WolverineOptions).Assembly.GetName().Version` → different fingerprint |

### Per fallback

| Test | What it asserts |
|---|---|
| **Stale snapshot → soft fallback** | Corrupt snapshot → host starts, warning logged with known signature, snapshot regenerated |
| **Missing snapshot → soft fallback** | Snapshot file absent → host starts, runtime codegen runs, snapshot written |
| **No production hard-fail** | Even with `AssertCodeGenerationStateAtStartup`-equivalent, snapshot rejection only warns |

---

## `PrewarmHotPaths()` API shape

Per the issue, opt-in:

```csharp
opts.PrewarmHotPaths();
```

Implementation: at `StartAsync`, walk every registered message type and force fill the lazy caches:

1. `MessageContext._typedEnumerableCascadeMethods` — call the existing prepopulate walker at line 799.
2. Per-handler routing decisions for `IAgentCommand`-derived types — pre-resolve `HandlerGraph._handlers` for the known derivations.
3. Post-#2724 `Endpoint._serializers` for content types that weren't known at compile time (e.g. tenant-injected content types) — N/A for most setups; document as advanced.

**Recommended default**: `false`. Long-running services should opt in; serverless / Lambda should leave off and pay first-message latency instead of cold-start latency.

**Docs ride-along** (already-closed #2717): the serverless section in the migration guide needs the tradeoff paragraph + log signature for the snapshot fallback.

---

## Optional source-generator applicator (step 7)

Under option B (generated partial-class registrations), the source generator is **not strictly required for the runtime path** — the generated partial class is compiled normally and discovered via the same assembly scan that already finds the handler types.

A source generator would still add value for:

- **Static-mode assertion**: emit a `[ModuleInitializer]` that registers the snapshot at module load, eliminating even the assembly-scan cost.
- **AOT trim-friendliness**: keep all generated types referenced from a single `[DynamicallyAccessedMembers]`-annotated root.

Mark this as step 7, follow-up. Don't block 1-6 on it.

---

## JasperFx-side API needs

The implementation will run into the JasperFx 2.0 codegen surface. Wolverine currently pins `JasperFx.Events 2.0.0-alpha.8`; the foundation re-align targets `2.0.0-alpha.13`. The right time to find API gaps is **after the foundation re-align lands**, against the stable surface.

Likely API needs (to file as separate JasperFx issues when they surface):

1. **A `ICodeFile`-equivalent for non-handler artifacts** — currently `ICodeFile` writes per-type C#; the snapshot partial class is per-application. May need a sibling abstraction or a "container" `ICodeFile`.
2. **A way to discover the generated snapshot type from runtime** — the existing `LoadPrebuiltTypes` (`DynamicCodeBuilder.cs:242`) handles handler types; needs an extension point or a known-name convention for the snapshot.
3. **Fingerprint hook** — codegen rule extension so a snapshot-writing custom `ICodeFile` can opt into the same `Internal/Generated/Wolverine/` output path.

None of these are blockers — they're all "could be a bit cleaner if JasperFx exposed X". The runtime path can be built without any JasperFx-side change by treating the snapshot as a pure Wolverine concern.

---

## Sequencing (refined from issue body)

The issue's 7-step sequence holds. Refinements:

| Step | Status | Refinement |
|---|---|---|
| 1. Land Tier 1 first | **#2724 done**, **#2726 open** | #2726 is "independent runtime-perf companion" per its description — not a strict prereq for snapshot work, but landing first reduces noise in cold-start benchmarks. Recommend completing it. |
| 2. Benchmark harness in CritterStackScalability | Not started | Cross-repo. **Must be in place before any snapshot PR merges** so PR comments can compare measured deltas. Out of this chip's scope. |
| 3. First PR — message-naming + handler-graph snapshot | **The chip's primary target** | Lowest risk. Two artifacts, both already have prepopulate walkers — snapshot replaces the prepopulate input with a frozen lookup. |
| 4. Second PR — endpoint serializer cache | Should land in parallel with step 3, not after | Already mostly read-only post-#2724. Snapshot value is incremental. |
| 5. Third PR — routing table | **Trust-gate suite is the blocker**, not the implementation | Multi-tenant + convention-based tests in `Persistence/MartenTests/Snapshot/` must be green before this lands. |
| 6. `PrewarmHotPaths()` opt-in | Can land **before** step 3 | API shape is small, no snapshot dependency, doc updates ride along. |
| 7. Optional source-generator applicator | Hold | After step 5 ships and is observed in user apps for a release cycle. |

**Recommended landing order** for the implementation phase:
1. `PrewarmHotPaths()` (step 6) — small, isolated, no snapshot needed.
2. Snapshot framework + fingerprint (no live artifacts) — establishes the codegen-extension pattern.
3. Message-naming snapshot (step 3a).
4. Handler-graph snapshot (step 3b).
5. Endpoint-serializer snapshot (step 4).
6. Routing-table snapshot (step 5) — gated on the trust-gate suite.
7. Source-generator applicator (step 7).

---

## Foundation re-align dependency

Wolverine `main` currently pins:
- `JasperFx 2.0.0-alpha.13`
- `JasperFx.Events 2.0.0-alpha.8`
- `JasperFx.RuntimeCompiler 5.0.0-alpha.1`
- `Marten 9.0.0-alpha.2`

The foundation re-align chip (`wolverine-foundation-realign.md`) targets:
- `JasperFx 2.0.0-alpha.14`
- `JasperFx.Events 2.0.0-alpha.13`  (← 5 alphas ahead, dedupe-pillar contracts)
- `JasperFx.RuntimeCompiler 5.0.0-alpha.2`
- `Marten 9.0.0-alpha.3` (← Phase 2 FEC retirement)
- `Weasel.* 9.0.0-alpha.5`

That chip is currently **paused** waiting for Marten 9.0-alpha.3 and Polecat 4.0-alpha.5 to publish to NuGet (upstream PRs merged, publishes haven't run as of 2026-05-19).

**Implication for this chip**: don't start step 3+ implementation PRs against alpha.8. JasperFx.Events between alpha.8 and alpha.13 lifted the dedupe-pillar contracts (IEventStream, IEventStoreOperations, etc.) and bumped the daemon-cancellation handling — the codegen surface is genuinely different. Building snapshot integration against alpha.8 risks rework when the re-align lands.

**Steps that are safe to land before the re-align**:
- Step 6 (`PrewarmHotPaths()`) — pure Wolverine, no JasperFx codegen dependency.
- This chip — design only, no code.

---

## Open questions for the next pass

1. **`Type` reference durability across snapshots** — if a snapshot is regenerated under Marten 9.0-alpha.3 but deployed against alpha.2 (mismatched env), the closed-generic `MessageRouter<TMessage>` references may not resolve. Fingerprint should catch this, but what's the fallback log signature?
2. **`IMessageRoutingConvention` extension authors with non-deterministic config** — e.g. an extension that reads connection state at startup. Fingerprint can hash the convention type's FQN but not its post-startup behavior. Recommend documenting "snapshot expects deterministic routing-convention config; non-deterministic conventions will always invalidate."
3. **Snapshot vs `[ModuleInitializer]` AOT path** — there are at least two ways to install the snapshot at boot (manual call from `StartAsync` vs `[ModuleInitializer]`). Trim/AOT preference depends on which the runtime can prove static. Defer to step 7 follow-up.
4. **Polecat / Marten compatibility** — Marten's mirror issue ([JasperFx/marten#4370](https://github.com/JasperFx/marten/issues/4370)) wants the same mechanism. Marten team plans to learn from Wolverine's implementation experience. Surface area for `WolverineExtension`-style integrations (`Wolverine.Marten`, `Wolverine.Polecat`) may need to opt into the snapshot.

---

## Acceptance for this chip

- This file exists, captures the artifact-level surface verified against `main`, names the trust-gate test categories per artifact, and proposes a sequencing that respects the foundation re-align timing.
- The next pass to start coding has a concrete "Step 6 first, then framework, then artifact-by-artifact" landing order.
- JasperFx-side API gaps are sketched but not filed — those happen against the post-re-align surface, not alpha.8.

## Out of scope for this chip

- Writing implementation code. This is design only.
- Filing JasperFx API-gap issues. Those wait for the post-re-align surface.
- The Outstanding-snapshot for CritterWatch — explicit non-goal per the issue.
- Benchmark harness setup in CritterStackScalability — cross-repo.
- Source-generator applicator design beyond "it's a follow-up under option B."

## Don't

- Don't start any step 3+ implementation PR before the foundation re-align lands.
- Don't try to land a single mega-PR with all snapshot artifacts. The issue explicitly sequences this as 5–7 separate PRs and the trust-gate suite needs to be reviewed per artifact.
- Don't suppress `IL2026` / `IL3050` in snapshot code paths to silence trim warnings — annotate the surface or narrow it, don't bypass.
- Don't bake routing-convention config into the fingerprint via reflection on private state. The fingerprint stays cheap, deterministic, and based on public registration shape only.
- Don't pre-warm by default — `PrewarmHotPaths()` is opt-in. The serverless tradeoff is real.
