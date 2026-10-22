# Durable receiver compliance rig

The durable inbox, exercised end to end and written as Bobcat specifications. It runs on four stores
(PostgreSQL, SQL Server, Marten, EF Core) over four endpoint kinds (a durable local queue, a database-backed
queue, RabbitMQ and SQS). It covers idempotency, logical deduplication, batching, error policies, one store per
tenant, and node lifecycle. Outside both solutions and CI: long running, and it needs every broker.

## How it judges a run

Every rig handler writes a **ledger** row (message id, attempt, node, tenant) inside its own transaction. After
each scenario, `the.TheDurableInvariantsHold()` waits for the run to settle and then checks every invariant
separately against the store and the ledger:

- every message has exactly one committed effect, or exactly one dead letter row
- no handler ran the same message at the same time as another (judged from the handlers' own timings)
- no inbox row is left unsettled, and nothing is left in the outbox
- every envelope carried its message's id. The row-level checks depend on this, and without it they pass
  without finding anything to judge.

Duplicates are allowed only when a store marks the inbox row handled outside the handler's transaction (the
plain RDBMS stores, `EffectSemantics.AtLeastOnce`) and something crashed or was released.

## Layout

- `Infrastructure/` contains the scripted handler behaviour (`Script`), the snapshot reader, the `Invariants`,
  the store and transport profiles, and the Bobcat vocabulary `DurableSteps`. `DurableSteps` is partial, so
  each scenario group adds its steps in its own file.
- `Stores/` holds one store profile per store, with its handlers and ledger writer.
- `Scenarios/` holds one abstract `*Compliance` base per scenario group with the `[BobcatSpec]` methods, plus
  bodyless `[BobcatFeature]` subclasses per store and transport.

## Running

    docker compose up -d postgresql sqlserver rabbitmq localstack
    ./rig.sh "FullyQualifiedName~settling_postgresql"

Findings are recorded in `DURABLE-COMPLIANCE-FINDINGS-2026-10-22.md` (repository root, untracked).
