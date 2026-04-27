# Order Fulfillment Process

A realistic Process Manager built on existing Wolverine + Marten features:

- `OrderFulfillmentState` is an event-sourced aggregate projected inline from the process's own stream.
- Six static handler classes, one per trigger message: start, payment confirmed, items reserved, shipment confirmed, cancel, and payment timeout. All keyed off the same stream.
- `[AggregateHandler]` on each continue handler class wires `FetchForWriting` + optimistic concurrency automatically. The start handler is a plain static class that returns `IStartStream` via `MartenOps.StartStream<T>`; see the Process Manager via Handlers guide in the Wolverine docs for why.
- The process reaches a terminal state by appending either `OrderFulfillmentCompleted` or `OrderFulfillmentCancelled`.

Start here:
- `OrderFulfillmentState.cs` — the state type and its `Apply` methods
- `Events.cs` / `Commands.cs` — the event and command records
- `Handlers/` — one handler per trigger message

This folder is the whole sample. The `Program.cs` at the project root is a thin hosting wrapper for demos.
