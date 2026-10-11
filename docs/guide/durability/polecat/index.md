# Polecat Integration

::: info
There is also some HTTP specific integration for Polecat with Wolverine. See [Integration with Polecat](/guide/http/polecat) for more information.
:::

[Polecat](https://github.com/JasperFx/polecat) and Wolverine are sibling projects under the [JasperFx organization](https://github.com/JasperFx), and as such, have quite a bit of synergy when used together. Adding the `WolverineFx.Polecat` Nuget dependency to your application adds the capability to combine Polecat and Wolverine to:

* Simplify persistent handler coding with transactional middleware
* Use Polecat and SQL Server as a persistent inbox or outbox with Wolverine messaging
* Support persistent sagas within Wolverine applications
* Effectively use Wolverine and Polecat together for a [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider) function workflow with event sourcing
* Selectively publish events captured by Polecat through Wolverine messaging
* Process events captured by Polecat through Wolverine message handlers through either [subscriptions](./subscriptions) or the older [event forwarding](./event-forwarding).

## Getting Started

To use the Wolverine integration with Polecat, install the `WolverineFx.Polecat` Nuget into your application. Assuming that you've configured Polecat in your application (and Wolverine itself!), you next need to add the Wolverine integration to Polecat as shown in this sample application bootstrapping:

```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddPolecat(opts =>
    {
        opts.Connection(connectionString);
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});
```

Using the `IntegrateWithWolverine()` extension method behind your call to `AddPolecat()` will:

* Register the necessary [inbox and outbox](/guide/durability/) database tables with Polecat's database schema management
* Adds Wolverine's "DurabilityAgent" to your .NET application for the inbox and outbox
* Makes Polecat the active [saga storage](/guide/durability/sagas) for Wolverine
* Adds transactional middleware using Polecat to your Wolverine application

### Optimized Envelope IDs for SQL Server <Badge type="tip" text="6.49" />

::: tip
**If Polecat -- and so SQL Server -- is the only message storage in your application, Wolverine does this for you.**
Polecat keeps Wolverine's envelopes in SQL Server, so at startup Wolverine switches to envelope ids that SQL Server
keeps in order, unless you've set `WolverineOptions.EnvelopeIdGeneration` yourself. No configuration, and no schema
change.
:::

SQL Server sorts Wolverine's default envelope ids in effectively random order, and the tables Wolverine uses for its
inbox and outbox are clustered on that id, so the default ids fragment those tables. The
[SQL Server integration page](/guide/durability/sqlserver#optimized-envelope-ids-for-sql-server) has the whole story.

If your application sends messages to other Wolverine services that store their envelopes in PostgreSQL, opt out,
because those services would use the SQL Server-ordered ids as *their* inbox keys, and PostgreSQL indexes them worse
than random GUIDs. The overload of `IntegrateWithWolverine()` that takes `optimizeEnvelopeIdsForSqlServer` does that
with `false` (and `true` forces the SQL Server-ordered ids):

<!-- snippet: sample_polecat_integration_with_sql_server_ordered_ids -->
<a id='snippet-sample_polecat_integration_with_sql_server_ordered_ids'></a>
```cs
opts.Services.AddPolecat(m =>
    {
        m.ConnectionString = Servers.SqlServerConnectionString;
        m.DatabaseSchemaName = "polecat_ordered_ids";
    })
    // This application sends messages to Wolverine services that store their
    // envelopes in PostgreSQL, so keep the default envelope ids
    .IntegrateWithWolverine(optimizeEnvelopeIdsForSqlServer: false);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PolecatTests/sql_server_ordered_envelope_ids.cs#L52-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_polecat_integration_with_sql_server_ordered_ids' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Setting `opts.EnvelopeIdGeneration` explicitly in your Wolverine configuration works too, and always wins.

## Transactional Middleware

See the [Transactional Middleware](./transactional-middleware) page.

## Polecat as Outbox

See the [Polecat as Outbox](./outbox) page.

## Polecat as Inbox

See the [Polecat as Inbox](./inbox) page.

## Saga Storage

See the [Polecat as Saga Storage](./sagas) page.
