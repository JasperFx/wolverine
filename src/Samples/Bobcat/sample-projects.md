# Critter Stack Migration — Sample Project Candidates

A curated inventory of open-source sample applications that are candidates for:

- **Porting to Marten and/or Wolverine** (event sourcing, modular monolith, DDD)
- **Migration source targets** (MassTransit → Wolverine, MediatR → Wolverine, Clean Architecture unraveling)

---

## Table of Contents

1. [Event Sourcing & CQRS Samples](#1-event-sourcing--cqrs-samples)
2. [Modular Monolith Samples](#2-modular-monolith-samples)
3. [MassTransit Migration Targets](#3-masstransit-migration-targets)
4. [MediatR / Clean Architecture Migration Targets](#4-mediatr--clean-architecture-migration-targets)
5. [ASP.NET Core Minimal API Samples](#5-aspnet-core-minimal-api-samples)
6. [License Summary](#6-license-summary)

---

## 1. Event Sourcing & CQRS Samples

### `oskardudycz/EventSourcing.NetCore`
**GitHub:** https://github.com/oskardudycz/EventSourcing.NetCore  
**License:** MIT  
**Priority:** ⭐ Highest

The most directly relevant event sourcing sample in the .NET ecosystem. Already uses Marten helpers (`WriteToAggregate`, `AggregateStream`) in several of its samples. Oskar Dudycz is deeply embedded in the Marten community.

**Domains covered:** Carts, Orders, Payments, Shipments, Sagas  
**Notable features:**
- Multiple sample styles: pure functions (no aggregates), classic aggregate, mixed ES + non-ES services
- EventStoreDB variants provide good "non-Marten" porting targets within the same repo
- Saga pattern with distributed processing and async command scheduling
- Self-paced workshop kit included
- Read models stored as Postgres tables using EF Core

**Porting angle:** Port the EventStoreDB-backed samples to Marten; use Wolverine to replace the custom dispatch infrastructure.

---

### `jasontaylordev/NorthwindTraders`
**GitHub:** https://github.com/jasontaylordev/NorthwindTraders  
**License:** MIT  
**Priority:** ⭐ High

Jason Taylor's original Clean Architecture sample on the iconic Northwind Traders database. More of a real application than the later template — covers customers, products, orders, and employees. Universally known domain makes a port immediately legible to any .NET developer.

**Stack:** ASP.NET Core, EF Core, MediatR, Clean Architecture  
**Porting angle:** Classic MediatR + Clean Architecture unraveling; add Marten event sourcing to the Orders domain.

---

### `andreschaffer/event-sourcing-cqrs-examples`
**GitHub:** https://github.com/andreschaffer/event-sourcing-cqrs-examples  
**License:** MIT  
**Priority:** Medium

A minimalistic bank context with clients, accounts, deposits, and withdrawals. Pragmatic CQRS where writes use aggregates and reads exist on both sides of the model.

**Domain:** Bank accounts, money transfers, transaction history  
**Notable features:** Commutative read model (handles out-of-order events), DDD + REST demonstrated together  
**Porting angle:** Clean, small domain — good starting point before tackling larger samples. Java-based, but domain is directly portable.

---

### `kbastani/event-sourcing-microservices-example`
**GitHub:** https://github.com/kbastani/event-sourcing-microservices-example  
**License:** Apache 2.0  
**Priority:** Low (Java/Spring)

Social network domain demonstrating CQRS + event sourcing with Kafka. Useful primarily as a domain reference — the friend relationship graph provides a high-complexity test for projection modeling.

---

## 2. Modular Monolith Samples

### `kgrzybek/modular-monolith-with-ddd`
**GitHub:** https://github.com/kgrzybek/modular-monolith-with-ddd  
**License:** MIT  
**Priority:** ⭐ Highest

One of the most-starred and most-cited modular monolith references in the .NET community. Production-quality code covering DDD tactical patterns, integration events, unit tests, integration tests, and an event store view.

**Domain:** Meeting group / scheduling (Meetup.com-like) — Administration, Meetings, Payments, UserAccess modules  
**Stack:** SQL Server, custom infrastructure (no MediatR/MassTransit — custom dispatcher)  
**Notable features:**
- Each module is fully encapsulated with its own schema
- Event sourcing present in one module (demonstrable before/after)
- Database change management via DbUp migrations
- CI/CD pipeline included

**Porting angle:** Replace custom infrastructure with Marten (event store) + Wolverine (messaging/dispatch). The custom outbox and inbox patterns map directly to Wolverine's durable messaging.

---

### `devmentors/Inflow`
**GitHub:** https://github.com/devmentors/Inflow  
**License:** MIT  
**Priority:** ⭐ High

Virtual payments app built as a modular monolith in .NET 6. Each module is an independent vertical slice with its own architecture, integrated primarily via events. No shared projects between modules — local contracts approach.

**Domain:** Virtual payments — Users, Wallets, Transfers, Reports modules  
**Stack:** PostgreSQL, RabbitMQ (in-memory for tests), custom modular framework  
**Notable features:**
- Zero inter-module project references — pure event-driven integration
- Branch showing module-to-microservice migration path
- Companion `Inflow-micro` repo for full microservices version

**Porting angle:** Replace RabbitMQ + custom event bus with Wolverine's transport abstraction. Replace custom persistence with Marten for event-sourced modules (Wallets, Transfers are natural ES candidates).

---

### `meysamhadeli/booking-modular-monolith`
**GitHub:** https://github.com/meysamhadeli/booking-modular-monolith  
**License:** MIT  
**Priority:** ⭐ High

Practical Modular Monolith on .NET 10 using Vertical Slice Architecture, Event Driven Architecture, CQRS, DDD, MassTransit, and Aspire. Uses an Event Store for the write side of the Booking module. Implements Inbox/Outbox for exactly-once delivery.

**Domain:** Travel/booking — Flight, Passenger, Booking, Identity modules  
**Stack:** MassTransit, MediatR, FluentValidation, Scalar/Swagger, OpenTelemetry, gRPC  
**Notable features:**
- Event sourcing already in place on the Booking module
- Inbox/Outbox implemented (direct Wolverine comparison)
- End-to-end tests with Testcontainers
- Husky + conventional commits enforced

**Porting angle:** Replace MassTransit → Wolverine, MediatR → Wolverine. Marten replaces the custom event store. This is one of the richest ports available.

---

### `kamilbaczek/Modular-monolith-by-example`
**GitHub:** https://github.com/kamilbaczek/Modular-monolith-by-example  
**License:** MIT  
**Priority:** Medium

Estimation tool for IT companies demonstrating modular monolith with DDD in .NET. Focuses on showcasing scalable, maintainable design with DDD principles.

**Domain:** IT project estimation workflows  
**Stack:** .NET, DDD patterns, modular architecture

---

### `NeVeSpl/TestMe`
**GitHub:** https://github.com/NeVeSpl/TestMe  
**License:** MIT (inferred from public repo)  
**Priority:** Medium

A quiz/test creation application using ASP.NET Core + PostgreSQL, vertical slices, outbox pattern for integration events, and an inbox for deduplication. Real-world quality with metrics endpoint.

**Stack:** ASP.NET Core, EF Core, PostgreSQL, RabbitMQ or in-memory bus  
**Notable features:** Optimistic concurrency, outbox/inbox patterns, CSS security policy, Storybook frontend

---

### `devmentors/NPay`  
**GitHub:** https://github.com/devmentors/NPay  
**License:** MIT  
**Priority:** Low-Medium

Mini-course companion for modular monolith fundamentals. Smaller scope than Inflow — good as a first porting exercise before Inflow.

---

## 3. MassTransit Migration Targets

> **Licensing Note:** MassTransit v9 has moved to a commercial license. v8 remains Apache 2.0 through at least end of 2026. All samples below use v8 or earlier and are safe for open-source use.

### `aspnetrun/run-aspnetcore-microservices`
**GitHub:** https://github.com/aspnetrun/run-aspnetcore-microservices  
**License:** MIT  
**Priority:** ⭐ Highest — Already uses Marten

E-commerce microservices application using ASP.NET Core Minimal APIs, .NET 8, MassTransit (RabbitMQ), MediatR, **and Marten** (for the Ordering service). This is the single most compelling migration target: Wolverine would replace both MassTransit *and* MediatR, and Marten is already partially in use.

**Domain:** E-commerce — Catalog, Basket, Discount, Ordering services  
**Stack:** Minimal API, Vertical Slice Architecture, MassTransit, MediatR, Marten, PostgreSQL, Redis, MongoDB, Yarp API Gateway  
**Notable features:**
- Marten already wired into one service
- BasketCheckout event flows from Basket → Ordering via MassTransit
- CQRS via MediatR with FluentValidation + Mapster
- Yarp reverse proxy with rate limiting

---

### `MassTransit/Sample-ShoppingWeb`
**GitHub:** https://github.com/MassTransit/Sample-ShoppingWeb  
**License:** Apache 2.0  
**Priority:** ⭐ High

ASP.NET web application with a simulated shopping cart using a MassTransit state machine saga to track cart expiration via Quartz.

**Domain:** Shopping cart with expiration / abandoned cart workflow  
**Porting angle:** Shopping cart saga maps cleanly to Wolverine's durable saga model. Small enough to be a clean teaching example.

---

### `MassTransit/Sample-Outbox`
**GitHub:** https://github.com/MassTransit/Sample-Outbox  
**License:** Apache 2.0  
**Priority:** ⭐ High

Demonstrates MassTransit's transactional outbox pattern.

**Porting angle:** Since Wolverine's outbox is one of its flagship features (built into Marten's event store integration), this makes for a tight, focused before/after comparison. Ideal as an isolated demonstration.

---

### `MassTransit/Sample-Batch`
**GitHub:** https://github.com/MassTransit/Sample-Batch  
**License:** Apache 2.0  
**Priority:** Medium

ASP.NET Core Web API demonstrating batch job processing via a MassTransit saga, with Swagger UI for triggering batches. Supports RabbitMQ or Azure Service Bus via configuration.

**Notable features:** Routing slips, activity compensation, configurable transport  
**Porting angle:** Covers advanced saga mechanics (routing slips, compensation) that Wolverine handles differently — good coverage of edge cases.

---

### `ebubekirdinc/SuuCat`
**GitHub:** https://github.com/ebubekirdinc/SuuCat  
**License:** MIT  
**Priority:** Medium

Microservices design patterns sample including Saga Orchestration via MassTransit state machine with RabbitMQ. OrderStateMachine coordinates Order → Stock reservation → Payment flow with rollback.

**Domain:** Order processing with stock and payment saga  
**Porting angle:** Good real-world saga orchestration example; the state machine maps directly to Wolverine's saga abstraction.

---

## 4. MediatR / Clean Architecture Migration Targets

> **Licensing Note:** MediatR is also moving toward commercialization (announced April 2025). There is an active community discussion in the `ardalis/CleanArchitecture` repo naming Wolverine as the leading open-source alternative.

### `jasontaylordev/CleanArchitecture`
**GitHub:** https://github.com/jasontaylordev/CleanArchitecture  
**License:** MIT  
**Priority:** ⭐ Highest

The most-starred Clean Architecture template in the .NET ecosystem (~16k stars). Uses MediatR throughout with pipeline behaviors for validation, logging, and performance. The canonical reference point for the pattern most teams are running today.

**Domain:** Todo list (simple, intentionally generic)  
**Stack:** ASP.NET Core, EF Core, MediatR, FluentValidation, AutoMapper, Angular/React frontend  
**Notable features:**
- `dotnet new ca-sln` template with Angular, React, or API-only options
- Supports PostgreSQL, SQLite, SQL Server
- Full CI/CD pipeline to Azure included
- Comprehensive test suite

**Unraveling story:** A typical use case has 5 artifacts (command, validator, handler, 2x pipeline behavior registrations). Wolverine collapses this to 1 handler class with inline `Validate()`. The diff is dramatic and highly bloggable.

---

### `ardalis/CleanArchitecture`
**GitHub:** https://github.com/ardalis/CleanArchitecture  
**License:** MIT  
**Priority:** ⭐ High

Steve Smith's Clean Architecture template (~16k stars). Uses a dedicated UseCases project organized by feature, with FastEndpoints for the Web layer rather than controllers. More opinionated than the Jason Taylor template, and has a companion **sample application** with richer domain (multiple aggregates, more DDD patterns) beyond the bare template.

**Stack:** ASP.NET Core, FastEndpoints, MediatR, EF Core, Ardalis.Result, Ardalis.GuardClauses  
**Notable features:**
- Commands use Repository pattern; Queries use direct data access (no repository required)
- FastEndpoints already thins the endpoint → mediator chain
- Active discussion thread on MediatR going commercial with Wolverine named as alternative

**Unraveling story:** Ardalis explicitly calls out double-validation (FluentValidation in FastEndpoints *and* in MediatR pipeline) as a known tradeoff. Wolverine's middleware eliminates this duplication entirely — a concrete before/after with a respected community voice already acknowledging the friction.

---

### `kgrzybek/sample-dotnet-core-cqrs-api`
**GitHub:** https://github.com/kgrzybek/sample-dotnet-core-cqrs-api  
**License:** MIT  
**Priority:** High

A REST API using MediatR for command/query/domain event handling, DDD on the write side with EF Core, and raw SQL via Dapper on the read side. Implements Cache-Aside pattern.

**Same author as:** `modular-monolith-with-ddd`  
**Porting angle:** Smaller scope than the full modular monolith — a good first porting exercise by the same author, before tackling the larger project. Clean, well-structured code.

---

### `gothinkster/aspnetcore-realworld-example-app`
**GitHub:** https://github.com/gothinkster/aspnetcore-realworld-example-app  
**License:** MIT  
**Priority:** High

ASP.NET Core implementation of the RealWorld spec ("Conduit" blog platform). CQRS and MediatR with feature folders, vertical slices, FluentValidation, AutoMapper, EF Core on SQLite, and JWT auth.

**Domain:** Blog platform — articles, comments, profiles, favorites, tags  
**Notable features:**
- Implements the RealWorld API spec — plug in any frontend (React, Vue, Angular, etc.)
- Complete UI available via the RealWorld frontend ecosystem
- Open to porting to other ORMs/DBs (explicitly stated in readme)

**Porting angle:** Well-defined API contract via the RealWorld spec provides a test harness for verifying the port. Any frontend continues to work unchanged.

---

### `mehdihadeli/vertical-slice-api-template`
**GitHub:** https://github.com/mehdihadeli/vertical-slice-api-template  
**License:** MIT  
**Priority:** Medium-High

Vertical Slice Architecture template on .NET 9. MediatR pipeline behaviors for FluentValidation, OpenTelemetry collector (Jaeger, Loki, Prometheus), PostgreSQL/EF Core, and multi-level tests.

**Porting angle:** Vertical slices already map naturally to Wolverine's handler model. Good for demonstrating that Wolverine doesn't just replace MediatR but also simplifies the pipeline behavior ceremony.

---

### `EduardoPires/EquinoxProject`
**GitHub:** https://github.com/EduardoPires/EquinoxProject  
**License:** MIT  
**Priority:** Medium

Full ASP.NET Core application with DDD, CQRS, and Event Sourcing using EventStoreDB (or in-memory). Well-known in the Brazilian .NET community.

**Porting angle:** EventStoreDB → Marten for event storage; MediatR → Wolverine for dispatch.

---

## 5. ASP.NET Core Minimal API Samples

### `matjazbravc/CQRS.MinimalAPI.Demo`
**GitHub:** https://github.com/matjazbravc/CQRS.MinimalAPI.Demo  
**License:** MIT (inferred)  
**Priority:** Medium

Focused demonstration of CQRS with Minimal API, MediatR, EF Core 7, and SQLite. Explicitly illustrates the Minimal API → MediatR → handler wiring pattern.

**Porting angle:** Clean before/after showing how Wolverine integrates with Minimal API endpoints without the mediator indirection.

---

### `isaacOjeda/MinimalApiArchitecture`
**GitHub:** https://github.com/isaacOjeda/MinimalApiArchitecture  
**License:** MIT  
**Priority:** Medium

Vertical Slice Architecture with CQRS via MediatR, FluentValidation, AutoMapper, EF Core, NSwag code generation, Serilog, Angular and Blazor frontends.

**Porting angle:** Full-stack Minimal API vertical slice — Wolverine handler replaces MediatR handler with no change to the endpoint or frontend.

---

### `CharlieDigital/dn8-modular-monolith`
**GitHub:** https://github.com/CharlieDigital/dn8-modular-monolith  
**License:** MIT  
**Priority:** Low-Medium

Simple project management application on .NET 8 showing modular monolith structure with clean module boundaries, DI, and in-process communication. Three entities: Project, WorkItem, User.

**Porting angle:** Very approachable starting point — good for introducing Wolverine's local messaging to an audience unfamiliar with the Critter Stack.

---

## 6. License Summary

| Repo | License | Safe to Use |
|------|---------|-------------|
| `oskardudycz/EventSourcing.NetCore` | MIT | ✅ |
| `kgrzybek/modular-monolith-with-ddd` | MIT | ✅ |
| `devmentors/Inflow` | MIT | ✅ |
| `meysamhadeli/booking-modular-monolith` | MIT | ✅ |
| `kamilbaczek/Modular-monolith-by-example` | MIT | ✅ |
| `NeVeSpl/TestMe` | MIT | ✅ |
| `aspnetrun/run-aspnetcore-microservices` | MIT | ✅ |
| `MassTransit/Sample-ShoppingWeb` | Apache 2.0 | ✅ |
| `MassTransit/Sample-Outbox` | Apache 2.0 | ✅ |
| `MassTransit/Sample-Batch` | Apache 2.0 | ✅ |
| `ebubekirdinc/SuuCat` | MIT | ✅ |
| `jasontaylordev/CleanArchitecture` | MIT | ✅ |
| `jasontaylordev/NorthwindTraders` | MIT | ✅ |
| `ardalis/CleanArchitecture` | MIT | ✅ |
| `kgrzybek/sample-dotnet-core-cqrs-api` | MIT | ✅ |
| `gothinkster/aspnetcore-realworld-example-app` | MIT | ✅ |
| `mehdihadeli/vertical-slice-api-template` | MIT | ✅ |
| `EduardoPires/EquinoxProject` | MIT | ✅ |
| `matjazbravc/CQRS.MinimalAPI.Demo` | MIT | ✅ |
| `isaacOjeda/MinimalApiArchitecture` | MIT | ✅ |
| `CharlieDigital/dn8-modular-monolith` | MIT | ✅ |
| `devmentors/Inflow` | MIT | ✅ |

### Licensing Advisories

- **MassTransit v9** has moved to a commercial license. All MassTransit samples listed here use v8 (Apache 2.0), which is supported through at least end of 2026. New projects should use Wolverine instead.
- **MediatR** has announced a move to commercial licensing (April 2025). The sample repos listed here are MIT licensed and remain safe to use; MediatR as a dependency is what will require attention.
- **NServiceBus** is RPL 1.5 licensed (not permissive). No NServiceBus sample applications are listed here as primary targets; the framework would be replaced entirely in any migration scenario.

---

## Recommended Starting Order

For building out a Critter Stack migration showcase, the following progression makes sense:

1. **`matjazbravc/CQRS.MinimalAPI.Demo`** — Smallest possible MediatR → Wolverine port. Establishes the pattern.
2. **`jasontaylordev/CleanArchitecture`** — Most recognizable MediatR codebase. Demonstrates the "unraveling" story at a widely-known template.
3. **`MassTransit/Sample-Outbox`** — Focused outbox comparison. Wolverine's outbox is a flagship differentiator.
4. **`aspnetrun/run-aspnetcore-microservices`** — Replaces both MassTransit and MediatR; already uses Marten. Highest real-world relevance.
5. **`kgrzybek/modular-monolith-with-ddd`** — Full modular monolith migration. Demonstrates Marten event sourcing + Wolverine messaging replacing custom infrastructure.
6. **`devmentors/Inflow`** — Rich payments domain; event-driven module integration via Wolverine transports.
7. **`meysamhadeli/booking-modular-monolith`** — Has event sourcing already in place; MassTransit + MediatR both replaced by Wolverine.

---

*Generated: April 2026*
