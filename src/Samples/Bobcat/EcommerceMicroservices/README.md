# E-Commerce Microservices (RabbitMQ) — Critter Stack Conversion

> **See also:** `EcommerceModularMonolith/` — the same domain collapsed into a single application with durable local queues.

## Original Project

**Repository:** [aspnetrun/run-aspnetcore-microservices](https://github.com/aspnetrun/run-aspnetcore-microservices)
**License:** MIT
**Stack:** ASP.NET Core 8, MediatR, MassTransit (RabbitMQ), Carter, EF Core (SQL Server), Marten (Catalog & Basket), gRPC (Discount), Redis, MongoDB, YARP API Gateway

A full e-commerce microservices application with 4 services, API gateway, and Razor Pages web app. The most compelling migration target in the inventory because it already uses Marten in two services.

## What Changed

### Architecture

| Aspect | Original | Converted |
|--------|----------|-----------|
| Projects | 12 (4 services × layers + BuildingBlocks + Gateway + WebApp) | 5 (4 services + Shared) |
| Ordering service | 4 projects (API, Application, Domain, Infrastructure) | 1 project |
| Databases | PostgreSQL + SQL Server + SQLite + Redis + MongoDB | PostgreSQL only (Marten) |
| Message bus | MassTransit + RabbitMQ | Wolverine cascading messages (+ RabbitMQ when needed) |
| CQRS dispatch | MediatR + pipeline behaviors | Wolverine handlers (direct) |
| Discount transport | gRPC + Protocol Buffers | Wolverine.HTTP (plain REST) |
| API Gateway | YARP reverse proxy | Removed (each service is independently deployable) |

### Per-Service Changes

**Catalog** (was ~15 files across handlers + endpoints):
- Already used Marten — kept as-is
- Replaced 6 MediatR handlers + 6 Carter endpoints with 6 Wolverine endpoints
- Added `[Entity]` for get-by-id, update, delete operations
- Now 5 files

**Basket** (was ~12 files + Redis caching layer):
- Already used Marten — kept as-is
- Replaced MediatR handlers + MassTransit publisher with Wolverine endpoints
- `CheckoutBasket` uses cascading message tuple `(bool, BasketCheckoutEvent)` instead of `IPublishEndpoint`
- Removed `IBasketRepository` / `CachedBasketRepository` decorator — direct Marten session
- Now 5 files

**Ordering** (was 4 projects, ~40 files with DDD + Clean Architecture):
- Collapsed Domain + Application + Infrastructure + API into 1 project
- Replaced EF Core (SQL Server) with Marten (PostgreSQL)
- Replaced DDD ValueObjects (OrderId, CustomerId, Address, Payment) with flat document properties
- Replaced MassTransit `IConsumer<BasketCheckoutEvent>` with Wolverine handler
- Eliminated: AuditableEntityInterceptor, DispatchDomainEventsInterceptor, 4 EF configurations, DbUp migrations
- Now 6 files

**Discount** (was gRPC service with Protocol Buffers):
- Replaced gRPC + Mapster + EF Core (SQLite) with Wolverine.HTTP + Marten
- Now 4 files

### Removed Entirely
- **BuildingBlocks** — MediatR CQRS interfaces, validation behavior, logging behavior, MassTransit extensions
- **YARP API Gateway** — rate limiting, reverse proxy configuration
- **Shopping.Web** — Razor Pages frontend with Refit HTTP clients
- **Redis** — caching layer for basket
- **MongoDB** — not used in the original (was in earlier versions)

### What Stays the Same
- Domain concepts: Products, Shopping Carts, Orders, Coupons
- Service boundaries: Catalog, Basket, Ordering, Discount
- Integration event flow: Basket checkout → Order creation
- REST API contracts (same routes, same payloads)

## Running

Requires PostgreSQL. Each service needs its own database. Configure connection strings in each service's `appsettings.json`, then run each service independently:

```bash
cd Catalog && dotnet run
cd Basket && dotnet run
cd Ordering && dotnet run
cd Discount && dotnet run
```

Each service has Swagger UI at `/swagger`.

## Specifications

`Tests/` holds Bobcat's projected (code-first) specs for the Catalog service: ordinary xUnit v3 tests
that render as Given/When/Then specifications, driving the service through Alba. `ProductSpecs` covers
create, list, read by id and by category, update and delete, and the 400s and 404s around them. Every
step is a method on that class with a `[Given]`, `[When]` or `[Then]` attribute, so calling it records
it.

```bash
dotnet test --project Tests                     # the specs (Postgres, catalog database)
BOBCAT_SPEC_CONSOLE=1 dotnet run --project Tests   # ...and print every spec as it rendered
```

The specs found two things the build never could. None of the four services could start: Wolverine 6
stopped shipping the runtime compiler in core, and each now references `WolverineFx.RuntimeCompilation`.
And the old `.feature` file had never run against this API -- it expected a 201 from `POST /products`,
which answers 200 with the stored product, and an update at `POST /products/{id}`, where the service
takes `PUT /products` with the id in the body.
