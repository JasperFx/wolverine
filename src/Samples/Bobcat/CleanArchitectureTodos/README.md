# Clean Architecture Todos — Critter Stack Conversion

## Original Project

**Repository:** [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)
**License:** MIT
**Stack:** ASP.NET Core 10, MediatR, FluentValidation, AutoMapper, EF Core, ASP.NET Identity

The most-starred Clean Architecture template in the .NET ecosystem (~16k stars). A Todo application organized across 5 projects (Domain, Application, Infrastructure, Web, Tests) with MediatR pipeline behaviors for validation, logging, authorization, and performance monitoring.

## What Changed

### Removed (5 projects collapsed to 1)
- **Domain project** — 12 files (base classes, value objects, domain events, enums)
- **Application project** — 28 files (commands, queries, handlers, validators, behaviors, DTOs, AutoMapper profiles)
- **Infrastructure project** — 11 files (DbContext, configurations, interceptors, identity)
- **MediatR** — 7 commands, 2 queries, 9 handlers, 5 pipeline behaviors
- **FluentValidation** — 4 validator classes
- **AutoMapper** — mapping profiles and DTOs
- **EF Core** — DbContext, entity configurations, migrations, interceptors
- **ASP.NET Identity** — ApplicationUser, IdentityService

### Added
- **Wolverine.Http** — `[WolverineGet]`, `[WolverinePost]`, etc.
- **Marten** — PostgreSQL document store

### Before vs After

| Aspect | Original (Clean Architecture) | Converted (Wolverine + Marten) |
|--------|------------------------------|-------------------------------|
| Projects | 5 (Domain, Application, Infrastructure, Web, Tests) | 1 |
| C# files | ~67 | 5 |
| NuGet packages | MediatR, FluentValidation, AutoMapper, EF Core, Identity | WolverineFx.Http, Marten |
| Pipeline behaviors | 5 classes (Logging, Validation, Authorization, Performance, Exception) | Inline in endpoints |
| Per-feature artifacts | ~5 files (command, validator, handler, DTO, mapper) | 1 endpoint method |
| Database | EF Core (SQLite/SQL Server/PostgreSQL) | Marten (PostgreSQL document store) |

### The "Unraveling" Story

In the original, creating a TodoList required:
1. `CreateTodoListCommand.cs` — the command record
2. `CreateTodoListCommandValidator.cs` — FluentValidation rules
3. `CreateTodoListCommandHandler.cs` — the handler (7 lines of actual logic)
4. `TodoListDto.cs` + AutoMapper profile — response mapping
5. Pipeline behavior registrations in `DependencyInjection.cs`

In the converted version, the same operation is a single static method in `TodoListEndpoints.cs` with inline validation — about 20 lines total including the validation.

### Architecture Decisions

**Document model instead of relational:** TodoList contains its Items as a nested collection in a single Marten document, eliminating the need for a separate TodoItem table, foreign keys, and navigation property configuration. This matches how the data is actually consumed (always as a list with its items).

**Inline validation instead of pipeline behaviors:** For a simple CRUD app, the MediatR pipeline behavior chain (logging → exception handling → authorization → validation → performance → handler) adds significant indirection. Wolverine supports middleware, but for this sample the validation is simple enough to inline.

**No separate DTO layer:** The endpoint methods return response records defined alongside the endpoint. AutoMapper configuration is replaced by a LINQ `Select` projection.

## Running

Requires PostgreSQL. Update the connection string in `appsettings.json`, then:

```bash
dotnet run
```

Swagger UI available at `/swagger`.
