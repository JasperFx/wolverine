# Process Manager via Handlers

This sample demonstrates the **process manager** pattern in Wolverine using only built-in primitives — no first-class `ProcessManager` base class is required. An order fulfillment workflow coordinates three parallel integration events (payment confirmed, items reserved, shipment confirmed) and completes when all three arrive, with a scheduled payment timeout that cancels the process if payment does not arrive in time.

The pattern is composed from:

- `MartenOps.StartStream<T>` — starts the Marten event stream that backs the process state
- `[AggregateHandler]` — loads the current state via `FetchForWriting`, appends returned events, and saves in one transaction
- `OutgoingMessages.Delay` — schedules the `PaymentTimeout` self-message
- Terminal and idempotency guards (`yield break`) inside each continue handler

## Prerequisites

PostgreSQL must be running. Start it with the Docker Compose file at the repo root:

```bash
docker compose up -d
```

## Running the sample

```bash
cd src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers
dotnet run
```

There is one HTTP endpoint:

- `POST /orders/start` — starts a new fulfillment process

```bash
curl -X POST http://localhost:5000/orders/start \
  -H "Content-Type: application/json" \
  -d '{"orderFulfillmentStateId":"<guid>","customerId":"<guid>","totalAmount":99.99}'
```

The remaining steps (`PaymentConfirmed`, `ItemsReserved`, `ShipmentConfirmed`, `CancelOrderFulfillment`) are dispatched as internal messages — in a real system these arrive from external sources via a transport. In the tests they are dispatched directly via `InvokeMessageAndWaitAsync`.

## Running the tests

```bash
cd src/Samples/ProcessManagerViaHandlers/ProcessManagerViaHandlers.Tests
dotnet test
```

All tests passing is the definition of "working as intended." The suite covers:

- **Unit tests** (`HandlerUnitTests.cs`) — pure-function tests over all continue handlers; no host, no Marten, no async
- **Start tests** (`when_starting_a_fulfillment.cs`) — verifies the stream is created with the correct initial event and that a duplicate start throws `ExistingStreamIdCollisionException`
- **Completion tests** (`when_completing_a_fulfillment.cs`) — drives all three gates in various orders and verifies `OrderFulfillmentCompleted` is appended exactly once
- **Cancellation tests** (`when_cancelling_a_fulfillment.cs`) — verifies explicit cancel and that post-terminal messages are ignored
- **Timeout tests** (`when_payment_times_out.cs`) — verifies the scheduler fires `PaymentTimeout`, cancels the process, and that payment arriving before the timeout silences the timer

The scheduler tests use a short `PaymentTimeoutWindow` (1 second) and a polling helper to wait for the handler to run. All tests should pass in under 30 seconds on a warm machine.

> **Note:** The `PaymentTimeout` scheduled message is held in memory in this sample. In production, configure a durable inbox (e.g. `opts.PersistMessagesWithPostgresql(...)`) so scheduled timeouts survive a process restart.

## Further reading

See [docs/guide/durability/marten/process-manager-via-handlers.md](../../../../docs/guide/durability/marten/process-manager-via-handlers.md) for the step-by-step recipe and the reasoning behind each design choice.
