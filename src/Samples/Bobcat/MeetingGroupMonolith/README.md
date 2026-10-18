# Meeting Group Modular Monolith — Critter Stack Conversion

## Original Project

**Repository:** [kgrzybek/modular-monolith-with-ddd](https://github.com/kgrzybek/modular-monolith-with-ddd)
**License:** MIT
**Stack:** SQL Server, custom MediatR infrastructure, Autofac, Quartz.NET, SqlStreamStore (event sourcing), custom outbox/inbox

One of the most-starred modular monolith references in .NET (~12k stars). A Meetup.com-like meeting group scheduling domain with 5 modules, DDD tactical patterns, event sourcing in the Payments module, and custom outbox/inbox for inter-module messaging.

## What Changed

### Removed (massive custom infrastructure)
- **Custom outbox** — `OutboxMessages` table + `ProcessOutboxCommand` Quartz job per module
- **Custom inbox** — `InboxMessages` table + `ProcessInboxCommand` Quartz job per module
- **Internal commands** — `InternalCommands` table + `ProcessInternalCommandsCommand` per module
- **InMemoryEventBus** — custom pub/sub dictionary
- **SqlStreamStore** — custom event store over SQL Server (Streams, Messages, Checkpoints tables)
- **Quartz.NET** — 3 recurring jobs per module (15 total)
- **Autofac** — custom MediatorModule, decorator chains, contravariant registrations
- **MediatR pipeline decorators** — UnitOfWork, Validation, Logging per module
- **SQL Server** — replaced by PostgreSQL (Marten)
- **DbUp migrations** — Marten auto-creates schemas

### Before vs After

| Aspect | Original | Converted |
|--------|----------|-----------|
| Projects | 20+ (4 per module × 5 modules + BuildingBlocks) | 1 |
| Custom infrastructure files | ~50 (outbox, inbox, internal commands, event bus) | 0 |
| Event store | SqlStreamStore (Streams + Messages tables) | Marten Event Store |
| Outbox/Inbox | Custom tables + Quartz polling jobs | Wolverine durable local queues |
| Scheduled jobs | Quartz.NET (15 jobs) | Wolverine message scheduling |
| Database | SQL Server | PostgreSQL (Marten) |
| DI container | Autofac with custom modules | Built-in .NET DI |

### Architecture

```
MeetingGroupMonolith/
  Program.cs                  ← single host, schema-per-module, durable local queues
  IntegrationEvents.cs        ← shared messages between modules
  UserAccess/                 ← User identity
  Registrations/              ← User registration flow
  Administration/             ← Meeting group proposals & approval
  Meetings/                   ← Meeting groups, meetings, attendance
  Payments/                   ← Subscriptions & fees (EVENT SOURCED via Marten)
```

### Event Sourcing (Payments module)

The Payments module uses Marten's event store instead of SqlStreamStore:

```csharp
// Start a subscription stream with a domain event
session.Events.StartStream<Subscription>(subscriptionId,
    new SubscriptionCreated(subscriptionId, payerId, "Monthly", expirationDate));

// Marten rebuilds state via Apply methods on the Subscription aggregate
public class Subscription
{
    public void Apply(SubscriptionCreated e) { /* set state */ }
    public void Apply(SubscriptionRenewed e) { /* update expiration */ }
    public void Apply(SubscriptionExpired _) { /* mark expired */ }
}
```

### Inter-Module Communication

Each integration event is routed to a module-specific durable local queue:

```csharp
opts.LocalQueue("meetings").UseDurableInbox();
opts.Publish(x => {
    x.Message<NewUserRegisteredEvent>();
    x.ToLocalQueue("meetings");
});
```

Messages survive process restarts via the Marten outbox — replacing the 15 Quartz polling jobs.

## Running

Requires PostgreSQL only.

```bash
dotnet run
```

Swagger UI at `/swagger`.
