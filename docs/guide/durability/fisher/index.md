# Fisher Integration <Badge type="tip" text="6.27" />

[Fisher](https://github.com/JasperFx/fisher) is the SQLite-backed document database and event store in
the [Critter Stack](https://github.com/JasperFx), and the third sibling alongside Marten and Polecat.
Adding the `WolverineFx.Fisher` NuGet to your application lets you combine the two to:

* Simplify persistent handler code with transactional middleware
* Use Fisher and SQLite as a persistent inbox and outbox for Wolverine messaging
* Support persistent sagas in Wolverine applications
* Use the [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
  function workflow with event sourcing — the same
  [`[WriteModel]` / `[ReadModel]` / `[DeciderFunction]`](/guide/handlers/persistence.html#event-sourced-models)
  vocabulary that works against Marten and Polecat
* Selectively publish events captured by Fisher through Wolverine messaging

## Why you would choose it

Fisher's database is a **file**, embedded in your process. There is no server to run, no container to
start, and no connection to a network. A Fisher-backed Wolverine service is a zero-infrastructure
deployable — which makes it a good fit for embedded and edge applications, desktop tools, single-node
services, and integration test suites that would otherwise need PostgreSQL or SQL Server running.

The tradeoff is the one SQLite always makes, and it shapes what this integration supports.

::: warning One writer per file
SQLite allows a single writer per database file. Wolverine's durability tables therefore commit on
**Fisher's own connection**, inside Fisher's transaction, through Fisher's
[transaction participants](https://fisher.jasperfx.net/documents/transaction-participants) — not
through a second connection to the same file. Two connections to one file are two writers, and the
second blocks on the first from inside the first one's transaction, which presents as a **hang**
rather than an error.

The integration is built this way for you. It is worth knowing about if you plan to write your own
tables alongside Fisher's in the same file: use a transaction participant there too.
:::

::: warning Solo durability mode only
Leader election and agent distribution require several nodes sharing one database, and a Fisher store
is a file. Configure `opts.Durability.Mode = DurabilityMode.Solo`. If you need several nodes over one
event store, use [Marten](/guide/durability/marten/) or [Polecat](/guide/durability/polecat/).
:::

## Getting Started

Install the `WolverineFx.Fisher` NuGet, then add the Wolverine integration behind your `AddFisher()`
call:

```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddFisher(opts =>
    {
        opts.Connection("Data Source=app.db");
    })
    .ApplyAllDatabaseChangesOnStartup()
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    // A Fisher store is one SQLite file, so one node owns it
    opts.Durability.Mode = DurabilityMode.Solo;

    opts.Policies.AutoApplyTransactions();
});
```

`IntegrateWithWolverine()` will:

* Register Wolverine's [inbox and outbox](/guide/durability/) tables in the same SQLite file Fisher owns
* Add Wolverine's durability agent for the inbox and outbox
* Make Fisher the active [saga storage](/guide/durability/sagas) for Wolverine
* Add transactional middleware using Fisher to your Wolverine application

## Event Sourcing

The aggregate handler workflow is the store-agnostic one in Wolverine core, so a handler written
against Fisher is the same handler you would write against Marten or Polecat:

```cs
public static class ShipOrderHandler
{
    // Wolverine loads the Order's event stream with concurrency protection, hands you the current
    // state, and appends whatever events you return back to that same stream.
    public static OrderShipped Handle(ShipOrder command, [WriteModel] Order order)
    {
        return new OrderShipped(DateTimeOffset.UtcNow);
    }
}
```

See [Event Sourced Models](/guide/handlers/persistence.html#event-sourced-models) for the full
vocabulary — `[WriteModel]`, `[ReadModel]`, `[DeciderFunction]` and `[DcbModel]`.

## Ancillary stores

A second Fisher store registered with `AddFisherStore<T>()` integrates the same way, and the
provider-agnostic `[Storage(typeof(IMyStore))]` attribute routes a handler to it without naming
Fisher anywhere in your code:

```cs
builder.Services.AddFisherStore<IPlayerStore>(opts =>
    {
        opts.Connection("Data Source=players.db");
    })
    .ApplyAllDatabaseChangesOnStartup()
    .IntegrateWithWolverine();

// ...and in a handler
[Storage(typeof(IPlayerStore))]
public static void Handle(RecordPlayerScore command, IDocumentSession session)
{
    session.Store(new Player { Id = command.Name, Score = command.Score });
}
```

Each store is **its own file**, which is what gets two concurrent writers out of SQLite rather than
having them contend on one. Wolverine's durability tables for an ancillary store live in that
store's file, alongside its documents and events.

## What is not supported yet

Wolverine.Fisher is deliberately narrower than the Marten and Polecat integrations in its first
release. The following are tracked as follow-up work rather than shipped-but-broken:

| Not yet supported | Why |
|---|---|
| Multi-tenancy | Fisher's tenancy is a **file per tenant**, so Wolverine's durability tables cannot follow a tenant across files without a second writer per file |
| Cluster durability modes | One file, one node — see above |
| Schema-scoped transport tables | SQLite has no schemas |

::: tip
Wolverine's durability tables go in SQLite's `main` schema. `MessageStorageSchemaName` only accepts
a schema SQLite actually knows — `main`, `temp`, or a database you have `ATTACH`ed — so unlike the
Marten and Polecat integrations there is no per-service schema isolation to configure. Isolate with a
separate **file** instead.
:::
