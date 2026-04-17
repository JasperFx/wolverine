# Fetching Query Specifications <Badge type="tip" text="5.x" />

Wolverine recognizes Marten query specifications — either
[compiled queries](https://martendb.io/documents/querying/compiled-queries.html)
(`ICompiledQuery<TDoc, TResult>`) or
[query plans](https://martendb.io/documents/querying/compiled-queries.html#query-plans)
(`IQueryPlan<T>` / `IBatchQueryPlan<T>`) — when they appear as handler
dependencies, executes them automatically, and **batches them into a single
Marten `IBatchedQuery` whenever possible**, including alongside `[Entity]`
and `[Aggregate]` loads on the same handler.

This gives you two ergonomics for the same underlying machinery:

1. **Return the specification directly from a `Load()` method.** Wolverine
   sees the return type and wires up execution + relay to `Handle` /
   `Validate` parameters.
2. **Annotate a handler parameter with `[FromQuerySpecification]`.**
   Wolverine constructs the spec from the message (and other variables
   in scope) and runs it.

Under the hood both paths produce the same codegen — specifications are
collected and run through a single `CreateBatchQuery()` whenever two or
more can be batched together.

## Returning a specification from `Load`

The handler's `Load` (or `LoadAsync`) method returns the specification
object. Wolverine recognizes the return type and executes it; the
materialized result is what the subsequent `Handle` / `Validate` method
receives.

```csharp
public class NoteByIdCompiled : ICompiledQuery<Note, Note?>
{
    public Guid NoteId { get; set; }

    public Expression<Func<IMartenQueryable<Note>, Note?>> QueryIs()
        => q => q.FirstOrDefault(x => x.Id == NoteId);
}

public class LoadOneNoteHandler
{
    // The return type IS the specification. Wolverine runs it and passes
    // the materialized Note? to Handle.
    public static NoteByIdCompiled Load(LoadOneNote cmd)
        => new() { NoteId = cmd.NoteId };

    public static void Handle(LoadOneNote cmd, Note? note)
    {
        // note is the materialized query result
    }
}
```

### Tuple returns — multiple specifications in one round-trip

When a `Load` returns a `ValueTuple` of specifications, Wolverine treats
each tuple element as its own specification and batches them:

```csharp
public class LoadNoteAndListStarredHandler
{
    public static (NoteByIdCompiled, StarredNotesPlan) Load(LoadNoteAndListStarred cmd)
        => (new() { NoteId = cmd.NoteId }, new());

    public static void Handle(LoadNoteAndListStarred cmd, Note? note, IReadOnlyList<Note> notes)
    {
        // Both the Note and the list arrive in one IBatchedQuery round-trip
    }
}
```

The generated code is roughly:

```csharp
var (spec1, spec2) = Handler.Load(cmd);
var batch = documentSession.CreateBatchQuery();
var noteTask  = batch.Query<Note, Note?>(spec1);
var notesTask = batch.QueryByPlan<IReadOnlyList<Note>>((IBatchQueryPlan<IReadOnlyList<Note>>)spec2);
await batch.Execute(cancellation);
var note  = await noteTask;
var notes = await notesTask;
Handler.Handle(cmd, note, notes);
```

## `[FromQuerySpecification]` — spec construction driven by the attribute

When you don't need a dedicated `Load` method — the specification's inputs
are all available on the message itself — attach
`[FromQuerySpecification(typeof(TSpec))]` to the handler parameter:

```csharp
public class LoadNoteViaAttributeHandler
{
    public static void Handle(
        LoadNoteViaAttribute cmd,
        [FromQuerySpecification(typeof(NoteByIdCompiled))] Note? note)
    {
        // Wolverine constructs new NoteByIdCompiled(), sets NoteId from
        // cmd.NoteId by name match, runs it, and passes the result here.
    }
}
```

Wolverine:

1. Picks the spec's public constructor with the most parameters
2. Resolves each constructor parameter from variables in scope (message
   members, route values, headers, claims) by case-insensitive name match
3. Assigns any remaining public settable properties on the spec from
   variables in scope — the canonical Marten compiled-query pattern where
   parameters are properties

On .NET 7+ you can use the generic variant for less ceremony:

```csharp
public static void Handle(
    LoadNoteViaAttribute cmd,
    [FromQuerySpecification<NoteByIdCompiled>] Note? note)
{
}
```

## Batching with `[Entity]`, `[Aggregate]`, and other specs

Specifications, `[Entity]` loads, and `[Aggregate]` loads all share the
same batching machinery. A handler that mixes them loads everything in
one `IBatchedQuery` round-trip:

```csharp
public class ApproveOrderHandler
{
    public static (NoteByIdCompiled, StarredNotesPlan) Load(ApproveOrder cmd)
        => (new() { NoteId = cmd.NoteId }, new());

    public static void Handle(
        ApproveOrder cmd,
        Note? note,                         // from compiled query
        IReadOnlyList<Note> starred,        // from query plan
        [Entity] Customer customer,         // from [Entity] by id
        [Aggregate] Order order)            // from [Aggregate] event stream
    {
        // All four loads executed in a single IBatchedQuery round-trip.
    }
}
```

## Which specifications can be batched?

| Specification kind | Batched? |
| ------------------ | -------- |
| `ICompiledQuery<TDoc, TResult>` | **Always** — via `IBatchedQuery.Query(compiled)` |
| A class that implements `IBatchQueryPlan<T>` (including anything deriving from `QueryListPlan<T>`) | **Always** — via `IBatchedQuery.QueryByPlan(plan)` |
| A class that implements only `IQueryPlan<T>` (no batch variant) | **Standalone** — runs via `session.QueryByPlanAsync(plan)` after the batch finishes |

When a plan cannot be batched, it executes as a separate round-trip; the
rest of the specifications on the same handler still share one batch.

**Tip:** if you're writing a plan that returns a list, prefer inheriting
from `QueryListPlan<T>` — it implements both `IQueryPlan<IReadOnlyList<T>>`
and `IBatchQueryPlan<IReadOnlyList<T>>`, so your plan is batch-capable for
free.

## When to reach for each approach

| Situation | Recommendation |
| --------- | -------------- |
| Load a single entity by primary key | `[Entity]` |
| Load an event-sourced aggregate | `[Aggregate]` / `[ReadAggregate]` / `[WriteAggregate]` |
| Reusable predicate used in multiple handlers | Extract to an `IQueryPlan<T>` and return from `Load` (or use `[FromQuerySpecification]`) |
| Hot, repeatedly-executed LINQ query | Extract to `ICompiledQuery<,>` and return from `Load` (or use `[FromQuerySpecification]`) |
| One-off LINQ that isn't reused elsewhere | Inline in the handler body |

**Refactor heuristic:** when you find the same `session.Query<T>().Where(...)`
expression in two or more handlers, extract it to a plan or compiled query.
Return it from `Load`. Wolverine will handle the rest — including batching
it with other loads on the same handler for a free performance win.

## Cross-provider parity

The same ergonomic applies to Wolverine.EntityFrameworkCore — Load methods
returning EF Core `IQueryPlan<TDbContext, TResult>` instances (or tuples of
them) auto-execute, and `[FromQuerySpecification]` works there too. See
[Query Plans (EF Core)](../efcore/query-plans.md) for the parallel story.
