# Outbox Demo — Critter Stack Conversion

## Original Project

**Repository:** [MassTransit/Sample-Outbox](https://github.com/MassTransit/Sample-Outbox)
**License:** Apache 2.0
**Stack:** MassTransit 8, EF Core (PostgreSQL), RabbitMQ, ASP.NET Core, OpenTelemetry/Jaeger

Demonstrates MassTransit's transactional outbox pattern: business data and messages are written in the same database transaction, guaranteeing at-least-once delivery without distributed transactions. Includes a state machine saga that orchestrates a multi-step registration workflow.

**Domain:** Event registration — a member registers for an event, triggering email notification, attendee tracking, and validation.

## What Changed

### Removed (3 projects collapsed to 1)
- **Sample.Api** — ASP.NET Core Web API with MVC controller
- **Sample.Components** — shared library with consumers, saga, services, contracts
- **Sample.Worker** — background service for consumer processing
- **MassTransit** — consumers, state machine saga, outbox configuration, consumer definitions
- **EF Core** — DbContext, entity configurations, saga state mapping, outbox/inbox tables
- **Separate worker process** — MassTransit required a separate worker for consumer hosting

### Added
- **WolverineFx.Marten** — integrated outbox (messages + documents in same transaction)
- **Wolverine handlers** — static methods replacing MassTransit consumers
- **Cascade messages** — handler return values automatically published via outbox

### Before vs After

| Aspect | Original (MassTransit) | Converted (Wolverine + Marten) |
|--------|----------------------|-------------------------------|
| Projects | 3 (Api, Components, Worker) | 1 |
| C# files | ~25 | 5 |
| Outbox config | EF Core interceptor + BusOutbox + polling interval | `IntegrateWithWolverine()` (one line) |
| Saga | State machine class + state entity + definition + map | Handler returning cascade tuple |
| Consumer | `IConsumer<T>` implementation class + definition class | Static `Handle(T)` method |
| Worker process | Separate hosted service | Built into web host |
| Inbox/Outbox tables | 4 MassTransit-managed tables | Wolverine envelope tables (auto-created) |

### The Outbox Comparison

**MassTransit outbox setup (spread across multiple files):**
```csharp
// In each host:
x.AddEntityFrameworkOutbox<RegistrationDbContext>(o => {
    o.QueryDelay = TimeSpan.FromSeconds(1);
    o.UsePostgres();
    o.UseBusOutbox();
});
// Plus: DbContext extends SagaDbContext, outbox tables in migrations
```

**Wolverine+Marten outbox setup (one line):**
```csharp
builder.Services.AddMarten(opts => { ... })
    .IntegrateWithWolverine();  // That's it. Outbox is built in.
```

### Saga Simplification

The original MassTransit saga required 4 files:
- `RegistrationStateMachine.cs` — state machine definition with transitions
- `RegistrationState.cs` — saga state entity
- `RegistrationStateDefinition.cs` — consumer retry and outbox config
- `RegistrationStateMap.cs` — EF Core mapping for saga state

The converted version is a single handler method that returns a tuple of cascade messages:
```csharp
public static (SendRegistrationEmail, AddEventAttendee) Handle(
    RegistrationSubmitted message, IDocumentSession session)
```

Wolverine sends both cascade messages via the Marten outbox in the same transaction.

## Running

Requires PostgreSQL. Update the connection string in `appsettings.json`, then:

```bash
dotnet run
```

POST to `/registration` with:
```json
{ "eventId": "conf-2026", "memberId": "member-1", "payment": 100.00 }
```

Swagger UI available at `/swagger`.
