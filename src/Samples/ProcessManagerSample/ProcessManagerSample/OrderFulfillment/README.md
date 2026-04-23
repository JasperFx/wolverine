# Order Fulfillment Process

A realistic Process Manager built on existing Wolverine + Marten features:

- `OrderFulfillmentState` is an event-sourced aggregate projected inline from the process's own stream.
- Each step (payment confirmed, items reserved, shipment confirmed, payment timeout) is a separate static handler keyed off the same stream.
- `[AggregateHandler]` on each handler class wires `FetchForWriting` + optimistic concurrency automatically.
- The process reaches a terminal state by appending either `OrderFulfillmentCompleted` or `OrderFulfillmentCancelled`.

Start here:
- `OrderFulfillmentState.cs` — the state type and its `Apply` methods
- `Events.cs` / `Commands.cs` — the event and command records
- `Handlers/` — one handler per trigger message

This folder is the whole sample. The `Program.cs` at the project root is a thin hosting wrapper for demos.
