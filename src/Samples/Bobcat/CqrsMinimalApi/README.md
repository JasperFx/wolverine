# CQRS Minimal API — Critter Stack Conversion

## Original Project

**Repository:** [matjazbravc/CQRS.MinimalAPI.Demo](https://github.com/matjazbravc/CQRS.MinimalAPI.Demo)
**License:** MIT
**Stack:** ASP.NET Core 8 Minimal API, MediatR 12, EF Core 8 (SQLite), MiniValidation

A CQRS demonstration using MediatR for command/query dispatch with a Student management domain. Shows the typical MediatR + Minimal API wiring pattern: endpoints call a service layer, which creates MediatR commands/queries and sends them through the mediator.

## What Changed

### Removed (6 layers of indirection)
- **MediatR** — 3 command classes, 3 query classes, 6 handler classes
- **Service layer** — `IStudentsService` / `StudentsService` mediator bridge
- **Repository layer** — `IBaseRepository<T>`, `BaseRepository<T>`, `IStudentsRepository`, `StudentsRepository`
- **EF Core** — `DataContext`, SQLite database, migrations
- **MiniValidation** — replaced by standard ASP.NET model binding

### Added
- **Wolverine.Http** — `[WolverineGet]`, `[WolverinePost]`, etc. endpoint attributes
- **Marten** — PostgreSQL document store, `IDocumentSession` / `IQuerySession`

### Before vs After

| Aspect | Original (MediatR) | Converted (Wolverine + Marten) |
|--------|-------------------|-------------------------------|
| Files | 22 C# files across 8 directories | 3 C# files in 1 directory |
| Request flow | Endpoint → Service → MediatR → Handler → Repository → EF Core | Endpoint → Marten session |
| Database | SQLite via EF Core + migrations | PostgreSQL via Marten (auto-schema) |
| Packages | MediatR, EF Core, MiniValidation | WolverineFx.Http, Marten |
| Identity type | `int` (auto-increment) | `int` (Marten HiLo) |
| Tests | None | Bobcat specifications (xUnit v3 + Alba) |

### Architecture

The original project demonstrated a 6-layer call chain for every operation:

```
Endpoint → IStudentsService → IMediator.Send() → IRequestHandler → IStudentsRepository → DbContext
```

The converted project collapses this to a single Wolverine HTTP endpoint method that directly uses Marten's document session:

```
[WolverinePost] endpoint method → IDocumentSession
```

Each endpoint is a static method in `StudentEndpoints.cs` with Wolverine HTTP attributes. Marten's `IDocumentSession` and `IQuerySession` are injected directly — no repository abstraction needed since Marten already provides a clean document store API.

### Bug Fix

The original `StudentsService.Create()` had Address and Email swapped in the command mapping. This was corrected in the conversion.

## Running

Requires PostgreSQL. Update the connection string in `appsettings.json`, then:

```bash
dotnet run
```

Swagger UI available at `/swagger`.

## Specifications

The specs in `Tests/` are Bobcat's projected (code-first) model: ordinary xUnit v3 tests that render as
Given/When/Then specifications. There is no `.feature` file anymore. `StudentSpecs` covers the five
scenarios -- create, list in name order, a 404 for a missing id, update, and delete-then-404 -- and every
step is a method on that class with a `[Given]`, `[When]` or `[Then]` attribute, so calling it records it.

```csharp
[BobcatSpec]
public async Task delete_student_and_verify_404()
{
    await GivenAStudent("Carol White", "carol@school.com");

    await WhenTheStudentIsDeleted();
    ThenTheResponseStatusIs(204);

    await WhenTheStudentIsRead(_studentId);
    ThenTheResponseStatusIs(404);
}
```

The host is booted once through Alba (`CqrsMinimalApiHost`), and `CqrsMinimalApiSpec` empties the store
before every spec -- the host seeds five students at start-up, and "at least 2 students are returned"
should mean the two the spec created.

```bash
dotnet test --project Tests                     # the specs
BOBCAT_SPEC_CONSOLE=1 dotnet run --project Tests   # ...and print every spec as it rendered
```
