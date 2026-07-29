# Handoff — three `Wolverine.RabbitMQ.Tests` tests fail round one on essentially every CI run

**Date:** 2026-07-29
**Start from:** `main` (at or after `7d825b01b`)
**Status:** root cause not established. Evidence gathered, hypotheses ranked, nothing fixed.

## The finding

`CIRabbitMQ` looks green on `main`, but it is not passing on the first attempt. The same three
tests fail round one and are then rescued by the flaky-retry harness, so the job reports success
and nobody sees it.

This surfaced while merging the xUnit v3 spillover work: PR #3722 went red on `CIRabbitMQ` and
looked like a regression. It was not. Comparing the two logs side by side:

| First-round failure | `main` @ `0a92b4cb7` | PR #3722 |
|---|---|---|
| `multi_node_exclusive_listener_failover.listener_fails_over_when_the_leader_running_it_crashes` | FAIL @ 1:37.78 | FAIL @ 1:35.71 |
| `Bugs.Bug_1594_ReplayDeadLetterQueue.can_replay_dead_letter_message(mode: BufferedInMemory)` | FAIL @ 4:32.95 | FAIL @ 4:29.44 |
| `ConventionalRouting.end_to_end_with_conventional_routing.send_from_one_node_to_another_all_with_conventional_routing` | FAIL @ 10:15.33 | FAIL @ 10:18.46 |

Same three tests. Nearly identical elapsed times. The **only** difference in outcome was retry luck:
`main`'s second round rescued all three, #3722's rescued two of three. PR #3722 touched no RabbitMQ
code at all — 16 files, none in the transport, and its `CITargets.cs` change was purely additive.

Jobs for reference:

- `main` @ `0a92b4cb7` — run `30494608184`, job `90720398967`, **conclusion: success**
- PR #3722 — run `30494220093`, job `90719159672`, **conclusion: failure**

Note what this means about the retry harness (see GH-3705): it is doing exactly what it was built
to do, and in doing so it has been hiding a chronic condition for an unknown length of time. Three
tests failing round one on *every* observed run is not flakiness in the usual sense.

## What each one reports

### 1. `listener_fails_over_when_the_leader_running_it_crashes` — this is GH-3604, now reproducible

```
System.TimeoutException : The exclusive listener agent never settled on a single surviving node
                          -- it kept flapping.
  at multi_node_exclusive_listener_failover.waitForStableListenerAsync(TimeSpan timeout)
     multi_node_exclusive_listener_failover.cs:line 152
  at multi_node_exclusive_listener_failover.listener_fails_over_when_the_leader_running_it_crashes()
     multi_node_exclusive_listener_failover.cs:line 271
```

**This is the most important item in this document.** GH-3604 was parked with PR #3610 as
*test-only*, on the grounds that "the flap is not reproducible on main." The assertion message here
is that flap, word for word, and it reproduces on CI on essentially every run. GH-3604 should be
re-opened / re-scoped on this evidence before anyone treats it as a test-only concern again.

Costs **~1m35s per failure** and it is attempted three times (once in-suite, twice on retry), so it
alone burns ~5 minutes of the job.

Uses a real Postgres message store, schema `multinode_listener_failover`.

### 2. `Bug_1594_ReplayDeadLetterQueue.can_replay_dead_letter_message(mode: BufferedInMemory)`

```
Shouldly.ShouldAssertException :
  afterIncoming.Any(env => env.Status == EnvelopeStatus.Incoming && env.Id == deadLetterId)
    should be True but was False
```

Fails in ~7s. Only the `BufferedInMemory` theory case is listed — worth confirming whether the
durable case passes, because if it does, the difference points at buffered-mode timing rather than
replay logic.

### 3. `end_to_end_with_conventional_routing.send_from_one_node_to_another_all_with_conventional_routing`

```
System.TimeoutException : This TrackedSession timed out before all activity completed.
```

Fails in ~5s. A tracked-session timeout, which given the xUnit v3 work is worth checking against
the pattern found in GH-3707/GH-3714: **a tracked session completes or times out based on the
conditions registered, and an operation that only enqueues needs an explicit condition.** Check
whether this one is a genuine cross-node delivery failure or a tracking-condition gap.

## Suggested order of work

1. **Reproduce locally first.** Everything above is from CI logs. Do not start from log reading —
   the last three times that shortcut was taken in this area it produced a wrong diagnosis.
   `docker compose up -d rabbitmq postgresql`, then run each test alone and in-suite.
2. **Separate "fails alone" from "fails in suite."** The retry harness re-runs a failed test *in
   isolation*, so a test that fails *because* it is isolated will fail the retry too — that is how
   #3722 went red while `main` went green. Establish which category each of the three is in; it
   changes the fix entirely.
3. **Take `listener_fails_over…` first** and tie it back to GH-3604. It is the slowest, the most
   likely to be a real product bug rather than a test problem, and the one with a standing issue
   whose central premise ("not reproducible") this evidence contradicts.
4. **Do not** paper over any of these with `[Trait("Category", "Flaky")]`. That is an exclusion,
   not a fix, and GH-3707 has already been through that cycle once.

## Things that will bite you

- **`git stash` reverts to HEAD, not `main`.** For a red baseline, check out `origin/main` explicitly.
- **RabbitMQ is stateful.** Residue from a previous run invalidates a baseline. Delete the queues
  between sides, or compare sibling branches cut from the same base.
- **Never quote a timing without a fresh container.**
- **`gh pr checks` mid-run lies** — wait for every check to reach a terminal state.
- Connection strings come from `Servers` (`src/Servers.cs`). Wolverine's Postgres is on **5433**.
- `gh issue view` has printed nothing at exit 0 in this repo; use `gh api` instead.

## Related

- **GH-3604** — exclusive-listener failover; PR #3610. Premise contradicted by item 1 above.
- **GH-3705** — the flaky-retry harness. Merged `7d825b01b`. Explains the masking.
- **GH-3707 / GH-3714** — tracked-session conditions and order-dependence. Relevant to item 3.
- **GH-3725** — unrelated follow-up: finish the xUnit1051 rollout.
