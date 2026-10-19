# Booking Modular Monolith — Critter Stack Conversion

## Original Project

**Repository:** [meysamhadeli/booking-modular-monolith](https://github.com/meysamhadeli/booking-modular-monolith)
**License:** MIT
**Stack:** .NET 10, MassTransit, MediatR, FluentValidation, EventStoreDB (event sourcing), EF Core (PostgreSQL), MongoDB (read models), gRPC (inter-module), Duende IdentityServer, .NET Aspire, Testcontainers

The richest original stack of all conversions. A travel booking system with 4 modules, event sourcing in the Booking module via EventStoreDB, MongoDB for read model projections, and gRPC for synchronous inter-module communication.

## What Changed

### Removed (7 infrastructure dependencies)
- **EventStoreDB** — replaced by Marten event store (same PostgreSQL)
- **MongoDB** — read model projections replaced by Marten inline snapshots
- **MassTransit** — replaced by Wolverine messaging + durable local queues
- **MediatR** — replaced by Wolverine handlers
- **gRPC inter-module calls** — replaced by direct Marten queries (same process)
- **Duende IdentityServer** — simplified to basic user registration
- **Custom outbox/inbox** — PersistMessageProcessor + ConsumeFilter replaced by Wolverine outbox

### Before vs After

| Aspect | Original | Converted |
|--------|----------|-----------|
| Projects | ~15 (modules × layers + BuildingBlocks + Aspire) | 1 |
| Databases | PostgreSQL + EventStoreDB + MongoDB | PostgreSQL only (Marten) |
| Event store | EventStoreDB (gRPC client) | Marten event store |
| Read models | MongoDB projections + EventStore subscriptions | Marten inline snapshots |
| Message bus | MassTransit (RabbitMQ/InMemory) | Wolverine durable local queues |
| CQRS | MediatR + pipeline behaviors | Wolverine handlers |
| Inter-module sync | gRPC proto + generated clients | Direct Marten queries |
| Identity | Duende IdentityServer | Simple user registration |

### Event Sourcing (Booking module)

The original used EventStoreDB with a custom `AggregateEventSourcing<T>` base class, stream naming conventions, MongoDB projections, and EventStore subscription checkpoints.

Marten replaces all of this with:
```csharp
// Event stream creation
session.Events.StartStream<BookingRecord>(bookingId, new BookingCreatedDomainEvent(...));

// Inline snapshot projection — no separate MongoDB read model needed
opts.Projections.Snapshot<BookingRecord>(SnapshotLifecycle.Inline);
```

### Inter-Module Communication

The original used gRPC for synchronous calls (Booking → Flight, Booking → Passenger). In a monolith, these are unnecessary — the Booking handler queries Marten directly since all modules share the same process.

## Running

Requires PostgreSQL only (no EventStoreDB, MongoDB, or RabbitMQ).

```bash
dotnet run
```

Swagger UI at `/swagger`.
