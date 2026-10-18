# Virtual Payments Monolith — Critter Stack Conversion

## Original Project

**Repository:** [devmentors/Inflow](https://github.com/devmentors/Inflow)
**License:** MIT
**Stack:** .NET 6, custom modular framework, EF Core (PostgreSQL), Chronicle sagas, custom outbox/inbox

A virtual payments app built as a modular monolith with zero inter-module project references. Each module is fully encapsulated with its own architecture. Integration is purely event-driven through a custom event bus, outbox, and inbox infrastructure.

**Domain:** Virtual payments — Users, Customers, Wallets (with transfers), Payments (deposits/withdrawals)

## What Changed

### Removed (custom modular framework)
- **20 projects** collapsed to 1
- **InMemoryEventBus** — custom pub/sub dictionary + module subscriptions
- **OutboxBroker + OutboxProcessor** — custom outbox tables + background polling
- **InboxEventHandlerDecorator** — custom inbox for idempotency
- **InternalCommands** — queued command pattern + background processor
- **ModuleClient + ModuleRegistry** — synchronous inter-module RPC
- **Contract validation** — schema validation for integration events
- **Chronicle** — distributed saga orchestration library
- **Scrutor decorators** — transactional, logging, inbox decorators
- **Custom dispatchers** — CommandDispatcher, QueryDispatcher, EventDispatcher, DomainEventDispatcher
- **AsyncMessageDispatcher + MessageChannel** — custom async message queue
- **ModuleLoader** — assembly scanning + dynamic module discovery
- **EF Core** — DbContexts, migrations, repositories per module

### Before vs After

| Aspect | Original | Converted |
|--------|----------|-----------|
| Projects | 20 | 1 |
| Custom infrastructure files | ~40+ | 0 |
| Outbox/Inbox | Custom tables + background processors | Wolverine durable local queues |
| Event bus | InMemoryEventBus + ModuleClient | Wolverine message routing |
| Sagas | Chronicle library | Wolverine cascading messages |
| DI framework | Custom module discovery + Scrutor | Built-in .NET DI |
| Database | EF Core (PostgreSQL) | Marten (PostgreSQL) |

### Module Communication

```
User Registration → UserCreated → Customers module (creates Customer stub)
                                    ↓
Customer Completion → CustomerCompleted → Wallets module (creates Wallet)
                                            ↓
Deposit/Transfer → FundsAdded/FundsDeducted → Payments module
```

All integration events flow through durable local queues — no custom outbox/inbox infrastructure needed.

## Running

Requires PostgreSQL only.

```bash
dotnet run
```

Swagger UI at `/swagger`.
