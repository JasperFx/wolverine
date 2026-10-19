# E-Commerce Modular Monolith — Critter Stack Conversion

> **See also:** `EcommerceMicroservices/` — the same domain split into separate services communicating via RabbitMQ.

## Original Project

**Repository:** [aspnetrun/run-aspnetcore-microservices](https://github.com/aspnetrun/run-aspnetcore-microservices)
**License:** MIT

Same domain as EcommerceMicroservices, but collapsed into a single application demonstrating the modular monolith pattern with Wolverine + Marten.

## Architecture

One host, one PostgreSQL database, four modules separated by folder and Marten schema:

```
EcommerceModularMonolith/
  Program.cs                 ← single host, schema-per-module Marten config
  IntegrationEvents.cs       ← shared messages between modules
  Catalog/                   ← Product CRUD
  Basket/                    ← ShoppingCart CRUD, checkout publishes BasketCheckoutEvent
  Ordering/                  ← Order CRUD, handles BasketCheckoutEvent
  Discount/                  ← Coupon CRUD
```

### Module isolation

Each module has its own Marten schema (`catalog`, `basket`, `ordering`, `discount`) within the same PostgreSQL database. Modules communicate through **durable local queues** — messages are persisted via the Marten outbox and survive process restarts.

```csharp
// Durable local queues for inter-module messaging
opts.LocalQueue("basket-checkout").UseDurableInbox();

// Route integration events to local queues
opts.Publish(x =>
{
    x.Message<BasketCheckoutEvent>();
    x.ToLocalQueue("basket-checkout");
});
```

### Microservices vs Modular Monolith

| Aspect | EcommerceMicroservices | EcommerceModularMonolith |
|--------|----------------------|------------------------|
| Deployment | 4 separate processes | 1 process |
| Database | 4 PostgreSQL databases | 1 database, 4 schemas |
| Transport | RabbitMQ | Durable local queues |
| Handler code | Identical | Identical |
| Infrastructure | Docker Compose + RabbitMQ | PostgreSQL only |

The handler code is the same in both versions — only `Program.cs` and the transport configuration differ. This demonstrates Wolverine's transport abstraction: swap local queues for RabbitMQ when you're ready to split into services.

## Running

Requires PostgreSQL only (no RabbitMQ needed).

```bash
dotnet run
```

Swagger UI at `/swagger`.
