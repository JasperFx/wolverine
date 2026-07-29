# xUnit v3 Migration Plan

**Status:** Plan — spiked and validated, decisions settled, ready to execute
**Date:** 2026-07-29
**Scope:** all 73 xUnit-referencing projects under `src/`, the Nuke build targets, and the GitHub Actions workflows
**Spike branch:** `spike/xunit3` (worktree at `.claude/worktrees/xunit3-spike`)
**Integration branch:** `feature/xunit3` (to be cut in wave 0)

---

## 1. Recommendation

Migrate to **`xunit.v3` 3.2.2 running in VSTest compatibility mode** (`xunit.runner.visualstudio` 3.1.5),
and **do not** adopt Microsoft.Testing.Platform (MTP) in the same change.

VSTest mode keeps `dotnet test --filter`, the TRX logger, `coverlet.collector`, and
`GitHubActionsTestLogger` working exactly as they do today — which means
**`build/Build.cs`, `build/CITargets.cs`, `build/TestAllPersistence.cs`, and all seven
workflows in `.github/workflows/` need no functional change at all.** The flaky-retry
harness (`RunWithFlakyRetry` → TRX parse → `FullyQualifiedName~` re-run) and the Polecat
namespace sharding both survive untouched.

MTP is the strategically correct end state and xunit is clearly steering there (as of 3.2.x,
`xunit.v3.core` is a thin shim over `xunit.v3.core.mtp-v1`), but adopting it simultaneously
would force a rewrite of the filter syntax, the TRX plumbing, the coverage collector, and
the GHA logger **in the same PR wave as a 73-project source migration**. Split it. See §8.

---

## 2. Spike results — what was actually measured

Everything below was run on this branch at `a524c09ea`, on `net9.0`, `-c Release`.

### 2.1 Pilot A — `Wolverine.DataAnnotationsValidation.Tests` (no `IAsyncLifetime`)

| Step | Result |
|---|---|
| `<OutputType>Exe</OutputType>` + swap `xunit` → `xunit.v3` | Build **FAILED**: 12 × `error xUnit1051` |
| Add `<NoWarn>$(NoWarn);xUnit1051</NoWarn>` | **Build succeeded**, zero other errors, zero warnings |
| `dotnet test --filter "Category!=Flaky" --logger "trx;..."` | Discovery, filter, and TRX output all work |
| Pass/fail parity vs. xunit 2 baseline | **6 passed / 6 failed on both sides** — identical |

> The 6 failures are pre-existing (`InvalidServiceLocationException`, `ServiceLocationPolicy.NotAllowed`)
> and reproduce identically on the unmigrated main tree. They are unrelated to xunit and are
> **not** in scope for this plan.

### 2.2 Pilot B — `Wolverine.ComplianceTests` + `SqliteTests` (the real shape)

`SqliteTests` was chosen because it inherits compliance base classes from another assembly
(`MessageStoreCompliance`, `TransportComplianceFixture`, `ExclusiveListenerRecoveryCompliance`)
and needs no Docker.

| Step | Result |
|---|---|
| `Wolverine.ComplianceTests` → `xunit.v3.extensibility.core`, drop `Microsoft.NET.Test.Sdk`, codemod 15 files | **Build succeeded** |
| `SqliteTests` → `xunit.v3` + `Exe` + codemod 15 files + **1 hand fix** | **Build succeeded** |
| `dotnet test --filter "Category!=Flaky" --logger trx` | **156 passed, 1 skipped, 157 total** |
| xunit 2 baseline, same command, same tree | **156 passed, 1 skipped, 157 total** |
| Duration | 1m09s (v2) vs 1m11s (v3) — no regression |

**Cross-assembly discovery of compliance base classes works.** That was the single biggest
architectural risk and it is now retired.

---

## 3. Inventory

73 projects reference xunit. They split into three classes:

| Class | Count | Treatment |
|---|---|---|
| Executable test projects | 72 | `xunit.v3` + `<OutputType>Exe</OutputType>` |
| **Shipped library** — `Wolverine.ComplianceTests` (`WolverineFx.ComplianceTests`) | 1 | `xunit.v3.extensibility.core`, stays a DLL |

Source churn, measured:

| Break | Sites | Files |
|---|---|---|
| `IAsyncLifetime` — `Task` → `ValueTask` on `InitializeAsync` | 545 async + 35 non-async | — |
| `IAsyncLifetime` — `Task` → `ValueTask` on `DisposeAsync` | 473 async + 71 non-async | — |
| `IAsyncLifetime` total | 626 | **541** |
| `using Xunit.Abstractions;` → `using Xunit;` (`ITestOutputHelper` moved) | 387 refs | **206** |
| `Assert.*` API changes | **22** | negligible — the suites are 9,587 Shouldly assertions |

Non-issues confirmed by inspection:

- **No `.fsproj` test projects.** The seven `*.FSharpTests` projects are C# drivers that
  shell `dotnet build` on checked-in F# fixtures. The xunit v3 F#-entry-point problem does not apply.
- **No custom xunit extensibility** — zero `DataAttribute`, `ITestFramework`, `XunitTestCase`,
  `IXunitSerializable`, `ITestCaseOrderer`, or `BeforeAfterTestAttribute` implementations.
  One incidental `using Xunit.Sdk`.
- **`xunit.assemblyfixture` is a dead reference.** Three projects (`CosmosDbTests`,
  `RavenDbTests`, `CosmosDbTests.LeaderElection`) carry the `PackageReference` with **zero**
  `AssemblyFixture` usages in source. Delete the references; v3's built-in
  `[assembly: AssemblyFixture(typeof(T))]` is available if anyone ever wants it.
- **Alba 8.5.2 does not depend on xunit** (verified against its nuspec) — no coupling.
- `[assembly: CollectionBehavior(...)]` (21 `NoParallelization.cs` files), `[Collection]` (235),
  and `[CollectionDefinition]` (27) are all unchanged in v3.

---

## 4. The `xUnit1051` decision

This is the one change with real blast radius, and it is a **policy** decision, not a mechanical one.

xUnit1051 — *"Calls to methods which accept CancellationToken should use
`TestContext.Current.CancellationToken`"* — is new in the v3 analyzer set, ships at warning
severity, and `Directory.Build.props` sets `TreatWarningsAsErrors=true` repo-wide. It fires on
**every** `await` of a method that has a `CancellationToken` overload: `host.StartAsync()`,
`Task.Delay(...)` (497 sites), `conn.OpenAsync()`, `client.SendAsync(...)`, and so on. A
498-line project with 17 files produced 12 errors; extrapolated across the suites this is
several thousand.

**DECIDED: suppress it repo-wide in wave 0, revisit as a follow-up.** Via a single
`Directory.Build.props` under `src/` (or the existing root file, conditioned on `$(TestProject)`):

```xml
<NoWarn>$(NoWarn);xUnit1051</NoWarn>
```

Rationale: honoring the rule properly means threading `TestContext.Current.CancellationToken`
through thousands of call sites, which is a genuine behavioral change to how tests cancel —
that is its own project with its own risk, and it should not be bundled into a framework swap.

**Wave 0 must therefore also file the follow-up issue** to bring the analyzer back, so the
suppression does not become permanent by default. That issue should propose re-enabling
per-project (smallest suites first) rather than repo-wide in one go.

---

## 5. The `WolverineFx.ComplianceTests` decision

`Wolverine.ComplianceTests` is packed and published (`build/Build.cs:395`) and is referenced by
**40 in-repo projects** plus an unknown number of community transport/persistence authors, who
are the entire reason it ships. It contains 178 `[Fact]`/`[Theory]` across 17 abstract base
classes and several open generics.

Migrating it to `xunit.v3.extensibility.core` **forces every downstream consumer onto xunit v3
at the same time.** There is no side-by-side story: a v2 test project cannot inherit from a v3
base class.

**DECIDED: ship v3-only in the next 6.x minor, signposted.** The package is a testing aid for
extension authors, not a runtime dependency; the ecosystem is small and reachable; and holding
72 projects hostage to a major version bump is disproportionate.

Consumers who are not ready pin `WolverineFx.ComplianceTests` **6.24.x** and stay there until
they move to xunit v3 themselves. That pin is the migration story and it must be stated
explicitly in the release notes — not left for people to discover at compile time.

Wave 1 therefore owes these deliverables beyond the code:

- a `CHANGELOG.md` entry naming the minimum xunit version and the 6.24.x pin — **done, wording
  is draft and needs Jeremy's review**;
- a callout in the release announcement — **outstanding**;
- ~~a `docs/guide/testing.md` update for extension authors~~ — **not applicable.** Checked:
  there is no extension-author page for `WolverineFx.ComplianceTests` anywhere in `docs/`. Every
  reference is an mdsnippet source link into `src/Testing/Wolverine.ComplianceTests/`. Writing
  one would be worthwhile but it is new documentation, not a migration edit — raise separately.

Rejected: a second package ID (`WolverineFx.ComplianceTests.V3`) — permanent dual identity and
disambiguation debt across every doc and sample, to spare a small audience a one-line pin.
Also rejected: multi-targeting v2/v3 off one project — both TFMs would need both frameworks,
so it needs a package-ID-suffix build trick that doubles CI for no real gain.

---

## 6. The codemod

A validated Python codemod exists at
`/private/tmp/claude-501/.../scratchpad/xunit3_codemod.py` (move to `build/` when work starts).
It handles, in order:

1. `Task` → `ValueTask` on `InitializeAsync`/`DisposeAsync` across **all** modifier orderings
   (`public virtual async`, `protected override`, …).
2. Explicit interface implementations: `Task IAsyncLifetime.DisposeAsync()` →
   `ValueTask IAsyncDisposable.DisposeAsync()` — v3's `IAsyncLifetime` derives from
   `IAsyncDisposable`, so the old explicit qualifier no longer names the declaring interface.
3. Expression bodies and `return`s: `Task.CompletedTask` → `ValueTask.CompletedTask` inside
   those two members only.
4. `using Xunit.Abstractions;` → `using Xunit;`, **deduped** — a naive replace produces CS0105,
   which `TreatWarningsAsErrors` turns into a build break.

It rewrote 15/48 files in ComplianceTests and 15/27 in SqliteTests with no manual correction
needed for those four categories.

**What it deliberately does not do:** a non-`async` `ValueTask InitializeAsync()` whose body is
`return SomeTaskReturningCall(...);` needs the method made `async` and the `return` turned into
`await`. The return expression is a `Task`, and rewriting it safely needs real syntax awareness.
Expect **~106 such sites** (35 non-async `InitializeAsync` + 71 non-async `DisposeAsync`, minus
the expression-bodied `=> Task.CompletedTask` cases the codemod already handles). Each is a
one-line hand fix; the compiler finds every one of them (`CS0029`).

---

## 7. Execution plan

**All waves land on the integration branch `feature/xunit3`, not on `main`.** One merge to
`main` at the end. Every wave is one PR into `feature/xunit3`, green-gated on its own CI
target(s) before the next starts. Waves 4–8 are independent of each other and can run in
parallel or be reordered freely.

This is forced by ComplianceTests: it fans out to 40 projects, and a v2 test project
referencing the v3 base classes will not compile, so waves 1–8 have to arrive together. See
§8.1 for the workflow trigger change this requires — **without it the wave PRs get zero CI.**

### Wave 0 — Foundation (no behavior change)

- Cut `feature/xunit3` from `main`, and add it to `pull_request.branches` in `tests.yml`,
  `dotnet.yml`, `http.yml` (see §8.1). **Do this first** — every later wave depends on it.
- `Directory.Packages.props`: add `xunit.v3` 3.2.2, `xunit.v3.extensibility.core` 3.2.2,
  `xunit.v3.assert` 3.2.2; bump `xunit.runner.visualstudio` 2.8.2 → **3.1.5** (3.x reads both
  v2 and v3, so this is safe while projects are still on v2).
- Add the `xUnit1051` suppression per §4, **and file the follow-up issue to claw it back.**
- Land the codemod under `build/`.
- Delete the three dead `xunit.assemblyfixture` references.
- **Gate:** full `wolverine.slnx` Release build still green. No test project has moved yet.

### Wave 1 — `Wolverine.ComplianceTests` + its cheapest consumer

- ComplianceTests → `xunit.v3.extensibility.core`, drop `Microsoft.NET.Test.Sdk`, run codemod.
- `SqliteTests` → `xunit.v3` + `Exe`.
- The three §5 deliverables: CHANGELOG entry, `docs/guide/testing.md`, release-note callout.
- **This wave is already done in the spike worktree and passes at exact parity.**
- **Gate:** `./build.sh CISqlite --framework net9.0`.

### Wave 2 — `CoreTests`

Pulled forward from the back of the plan and given its own PR. `CoreTests` is 402 files /
48 `IAsyncLifetime` / 16 `Xunit.Abstractions` — the second-heaviest suite in the repo — and
**three transport suites `ProjectReference` it** (`RabbitMQ.Tests`, `AmazonSqs.Tests`,
`AmazonSns.Tests`), so it blocks waves 6–7 no matter where it sits. Better to hit it early,
while there is room to recover, than at the end.

⚠️ **This wave is the first to exercise an Exe project referencing another Exe project**, which
the spike did *not* cover. It is legal in the SDK, and xunit v3 namespaces its generated entry
point under `$(RootNamespace)` so no `Program` collision is expected — but verify it on the
first transport suite in wave 6 before committing to the rest of that wave. If it does break,
the fallback is to split the shared fixtures out of `CoreTests` into a small
`xunit.v3.extensibility.core` library, exactly as ComplianceTests is handled.

- **Gate:** `./build.sh ci`, plus a build (not run) of the three dependent transport suites.

### Waves 3–8 — The remaining suites, grouped by CI target

Ordered smallest-blast-radius first so problems surface cheap. Counts are
`.cs` files / files touching `IAsyncLifetime` / files touching `Xunit.Abstractions`.

| Wave | Projects | Size | CI gate |
|---|---|---|---|
| **3. Long tail** | 5 claim-check, 8 samples, 7 F# drivers, `PolicyTests`, `MetricsTests`, `MessageRoutingTests`, `BackPressureTests`, `TracingTests`, `HealthChecks`, 4 extension suites, 6 LeaderElection, `MartenSubscriptionTests` | ~36 projects, ≤9 files each | `ci`, `CIMessageRouting` |
| **4. Persistence** | `PersistenceTests` 23/5/3 · `PostgresqlTests` 63/27/10 · `SqlServerTests` 64/23/3 · `MySqlTests` 19/5/2 · `OracleTests` 18/3/1 · `EfCoreTests` 48/15/7 · `EfCoreTests.MultiTenancy` 20/6/0 · `CosmosDbTests` 22/2/0 · `RavenDbTests` 31/8/1 | 9 projects | `CIPersistence`, `CISqlServer`, `CIMySql`, `CIOracle`, `CIEfCore`, `CICosmosDb`, `CIRavenDb` |
| **5. Marten + Polecat** | `MartenTests` **235/97/26** · `PolecatTests` 74/36/0 · `PolecatIncidentService.Tests` | 3 projects, the heaviest single suite | `CIMarten`, `CIPolecat*` (3 shards) |
| **6. Transports A** | `RabbitMQ.Tests` 107/40/19 · `CircuitBreakingTests` 16/3/9 · `ChaosTesting` 14/0/3 · `Kafka` 53/20/14 · `AzureServiceBus` 59/25/5 | 5 projects | `CIRabbitMQ`, `CICircuitBreaking`, `CIKafka`, `CIAzureServiceBus` |
| **7. Transports B** | `AmazonSqs` 53/18/4 · `AmazonSns` 21/9/0 · `Pubsub` 34/15/1 · `MQTT` 25/13/12 · `Mqtt5` 25/13/12 · `Nats` 24/10/9 · `Pulsar` 35/7/1 · `Redis` 36/10/9 · `SignalR` 13/1/0 | 9 projects | `CIAWS*`, `CIPubsub`, `CIMQTT`, `CIMQTT5`, `CINATS`, `CIPulsar`, `CIRedis` |
| **8. HTTP + gRPC** | `Http.Tests` 190/9/7 · `Http.AspVersioning.Tests` 16/2/0 · `Grpc.Tests` 86/18/0 · `SlowTests` 17/6/3 | 4 projects | `CIHttp`, `CIHttpAspVersioning`, `CIGrpc` |

**Remaining ordering constraints:**

- Waves 6 and 7 depend on **wave 2** (`CoreTests`).
- `RavenDbTests.LeaderElection` references `RavenDbTests` — both are in wave 4, keep them in
  the same PR.

### Wave 9 — Merge to `main`

- Revert the `pull_request.branches` trigger additions from wave 0.
- Single merge `feature/xunit3` → `main`.
- **Gate:** the full 27-way `tests.yml` matrix green on `main`, at parity with the baselines
  captured per §8.2.

### Wave 10 — Cleanup

- Delete the vestigial `disable_test_parallelization: true` env var from all three workflows
  (`tests.yml:12`, `dotnet.yml:12`, `http.yml:12`) — **it is set but read by nothing** in the
  repo. Parallelization is actually controlled by the 21 `NoParallelization.cs` assembly attributes.
- Add the missing `<IsPackable>false</IsPackable>` to `SlowTests` (currently the only
  non-shipped test project without it).
- Drop the now-unneeded `xunit` 2.9.3 `PackageVersion` from `Directory.Packages.props`.
- Update `docs/guide/testing.md` and `CLAUDE.md` (test conventions section).

---

## 8. CI tasks

**Phase 1 (this migration) needs exactly one workflow change, and zero Nuke changes.** The
workflow change is a consequence of the integration-branch decision, not of xunit v3 — the test
plumbing itself is untouched. That is the point of choosing VSTest mode, and it was verified
end-to-end in the spike.

### 8.1 The one required change: CI on the integration branch

All seven workflows trigger on:

```yaml
on:
  pull_request:
    branches: [ main ]
```

`pull_request.branches` filters on the **base** branch. A PR targeting `feature/xunit3`
therefore matches nothing and **runs no CI at all** — the wave PRs would merge unverified.

Wave 0 adds the integration branch to the three test workflows:

```yaml
  pull_request:
    branches: [ main, feature/xunit3 ]
```

in `.github/workflows/tests.yml`, `dotnet.yml`, and `http.yml`. Wave 9 reverts it. The other
four workflows (`docs.yml`, `command-line.yml`, `fsharp.yml`, `publish_nugets.yml`) do not gate
test results and can be left alone.

> Do **not** reach for `workflow_dispatch` as a substitute. It exists on all three workflows,
> but a manual dispatch runs against a branch rather than the PR merge commit, so it does not
> verify what would actually land.

### 8.2 Everything else keeps working unchanged

All of the following need zero edits:

| Mechanism | Location | Status under v3 + VSTest |
|---|---|---|
| `DotNetTest(...).SetFilter("Category!=Flaky")` | `TestAllPersistence.cs:187` | ✅ verified |
| TRX logger + `ParseFailedTestNamesFromTrx` | `TestAllPersistence.cs:125,188` | ✅ verified |
| Flaky-retry re-run via `FullyQualifiedName~` | `TestAllPersistence.cs:171` | ✅ same filter grammar |
| Polecat namespace sharding (`FullyQualifiedName!~ns.`) | `CITargets.cs:690` | ✅ same filter grammar |
| `--framework net9.0` / `net10.0` pinning | throughout | ✅ unchanged |
| `coverlet.collector` 6.0.4 (38 projects) | VSTest data collector | ✅ VSTest-only, still valid |
| `GitHubActionsTestLogger` 2.4.1 (57 projects) | VSTest logger | ✅ VSTest-only, still valid |
| 27-way `tests.yml` matrix, 20-min timeout | `.github/workflows/tests.yml` | ✅ unchanged |

### 8.3 The rest of the CI work is verification, not modification

1. **Per-wave gate.** Each wave PR runs its named CI target(s) from §7 and must reach **exact
   pass/skip/fail parity** with a baseline captured from `origin/main` on the same runner —
   not merely "green". Capture the baseline *before* the wave lands. Per
   `feedback_verify_baseline_against_origin_main`, `git stash` reverts to HEAD, not main; take
   the baseline from a clean `origin/main` checkout.
2. **Watch total wall-clock.** The 20-minute `timeout-minutes` in `tests.yml` is deliberately a
   real signal (see #3350). xunit v3's per-assembly process model has different startup cost;
   SqliteTests showed +2s on 157 tests, but `MartenTests` and `CoreTests` are 10–20× larger.
   If a shard newly exceeds 20 minutes, **split the shard** — do not raise the number.
3. **`Wolverine.ComplianceTests` no longer carries `Microsoft.NET.Test.Sdk`.** Confirm the
   `Pack` target (`build/Build.cs:353`) still produces a valid `WolverineFx.ComplianceTests`
   nupkg and that its dependency group now lists `xunit.v3.extensibility.core` instead of `xunit`.
4. **`CIAotSmoke`** (`CITargets.cs:621`) does `dotnet build`/`dotnet run` on the smoke projects,
   not `dotnet test` — unaffected, but re-run it after wave 0 since `Directory.Packages.props` moves.
5. **No new workflow file, no new matrix entry, no `actions/setup-dotnet` change.** The .NET 10
   SDK's opt-in `dotnet test` MTP mode (`TestingPlatformDotnetTestSupport`) stays **off**.
6. **Merge gating stays manual.** Per `feedback_green_gated_merge`, never use `--auto` here:
   with no required checks configured, it merges before CI finishes — which on an
   integration-branch strategy would silently poison every downstream wave.

### Phase 2 (deferred, separate issue): Microsoft.Testing.Platform

Not part of this migration. When it happens it requires, at minimum:

- `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` per project.
- Filter syntax rewrite across `CITargets.cs` and `TestAllPersistence.cs` — MTP does not
  accept VSTest's `--filter` grammar, so `Category!=Flaky`, the Polecat namespace shards, and
  the flaky-retry `FullyQualifiedName~` re-runs all need reworking.
- `Microsoft.Testing.Extensions.TrxReport` (currently 2.3.3) to replace the TRX logger, and a
  re-check that `ParseFailedTestNamesFromTrx` still parses its output.
- Replace `coverlet.collector` with `Microsoft.Testing.Extensions.CodeCoverage`.
- Bump `GitHubActionsTestLogger` 2.4.1 → 3.0.5 and verify MTP support.

The payoff is faster startup, native `dotnet run` on test executables, and alignment with where
xunit is heading. The cost is a second, independent CI-plumbing project. Keep them apart.

---

## 9. Decisions — settled 2026-07-29

| # | Decision | Resolution |
|---|---|---|
| 1 | `WolverineFx.ComplianceTests` becomes xunit-v3-only (§5) | **Ship in the next 6.x minor**, signposted. Consumers who aren't ready pin 6.24.x. Rejected: second package ID; holding for 7.0. |
| 2 | `xUnit1051` policy (§4) | **Suppress repo-wide in wave 0**, revisit later. Wave 0 files the follow-up issue so it doesn't become permanent by default. |
| 3 | Landing strategy | **Integration branch `feature/xunit3`**, one merge to `main` at the end. Requires the §8.1 workflow trigger change, reverted in wave 9. |
| 4 | `CoreTests` ordering | **Pulled forward to wave 2**, its own PR. It blocks waves 6–7 regardless, and it's the second-heaviest suite — hit it early. |

Nothing is currently blocking execution.

**Carried risks** (none blocking, all with a known fallback):

- **Exe→Exe project references** are first exercised in wave 2 and not covered by the spike.
  Fallback: split the shared `CoreTests` fixtures into an `xunit.v3.extensibility.core` library.
- **20-minute CI shard timeout** vs. v3's per-assembly process model. The spike measured +2s on
  157 tests, but `MartenTests` (235 files) and `CoreTests` (402 files) are far larger. Fallback:
  split the shard, per #3350 — do not raise the number.
- **~106 non-async `return`-shape sites** the codemod deliberately won't touch (§6). Not a risk
  to correctness — the compiler flags every one as `CS0029` — but it is unbudgeted hand-editing
  spread across waves 3–8.

---

## 10. Non-goals

- Adopting Microsoft.Testing.Platform (see §8, Phase 2).
- Fixing `xUnit1051` properly by threading `TestContext.Current.CancellationToken`.
- The 6 pre-existing `ServiceLocationPolicy.NotAllowed` failures in
  `Wolverine.DataAnnotationsValidation.Tests` — present on `main`, unrelated.
- Migrating away from Shouldly, NSubstitute, or the 3 remaining FluentAssertions files.
- Consolidating the 21 `NoParallelization.cs` files or revisiting the parallelization strategy.
- Reworking the flaky-retry harness or the CI sharding introduced in #3350.
