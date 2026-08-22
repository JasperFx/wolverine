# Managing Message Storage

::: info
Wolverine will automatically check for the existence of necessary database tables and functions to support the
configured message storage, and will also apply any necessary database changes to comply with the configuration automatically.
:::

Wolverine uses the [JasperFx "Stateful Resource"](https://github.com/JasperFx/jasperfx) model for managing
infrastructure configuration at development or even deployment time for configured items like the database-backed message storage or
message broker queues.

## Schema Names

Every `PersistMessagesWithXXX()` overload takes an optional schema name for Wolverine's tables, as do the
database-backed transports. That name has to be a **single database identifier**. Wolverine renders each of its
tables as `{schema}.{table}`, so a multi-part value like `crm.sales.opportunities` would produce
`crm.sales.opportunities.wolverine_incoming_envelopes` — a name with more parts than any supported database engine
accepts. SQL Server reports *"The object name contains more than the maximum number of prefixes. The maximum is 2"*
and PostgreSQL reports *"improper qualified name (too many dotted names)"*.

The database itself is chosen by the connection string, never by the schema name:

```csharp
// ✅ the schema "sales", inside whatever database the connection string names
opts.PersistMessagesWithSqlServer(connectionString, "sales");

// ❌ throws immediately with an ArgumentOutOfRangeException
opts.PersistMessagesWithSqlServer(connectionString, "crm.sales.opportunities");
```

Wolverine rejects a multi-part schema name at the point where it is configured rather than letting it fail later as
unusable DDL. Delimiting the name yourself — `"[crm.sales]"` on SQL Server — is rejected too, and does not work if
you route around the check: the schema is created, but the `CREATE SCHEMA` guard compares `sys.schemas.name` against
the spelling it was handed, brackets and all, so it never matches and every restart after the first re-issues the
`CREATE SCHEMA` against a schema that now exists. Schema difference detection cannot match a delimited name against
the catalog either, so such a store re-applies its whole DDL on every start and never picks up a later
column-level change.

## When a Migration Fails <Badge type="tip" text="6.30" />

If a DDL statement in Wolverine's own storage migration fails, startup fails with a `WolverineSchemaException` that
carries the exact SQL that could not be applied. Before 6.30 those failures were only logged, so a host could start
up against storage that had never been created and then die much later against an error that named nothing you had
configured.

Hosts that would rather start up anyway — a replica in a rolling deploy where another node is mid-migration, for
instance — can keep the older, tolerant behavior by setting the failure mode:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.ResourceMigrationFailureMode = ResourceMigrationFailureMode.ContinueOnFailures;
});
```

Note that Wolverine already serializes migrations across processes with a global advisory lock and retries the whole
migration once after a short delay, so a genuine race between two nodes starting at the same instant is resolved
without this setting.

## Disable Automatic Storage Migration

To disable the automatic storage migration, just flip this flag:

<!-- snippet: sample_disable_auto_build_envelope_storage -->
<a id='snippet-sample_disable_auto_build_envelope_storage'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Disable automatic database migrations for message
        // storage
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DisablingStorageConstruction.cs#L11-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_auto_build_envelope_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Disabling the automatic migration only affects the *passive* paths — host startup and tenant store discovery. An
explicit setup operation (`dotnet run -- resources setup` or `IHost.SetupResources()`) is treated as intent to
provision the storage, so it always applies the message storage migration as `CreateOrUpdate` — even when
`AutoBuildMessageStorageOnStartup` or the store's `AutoCreate` is `None`. `CreateOrUpdate` never drops existing
data. This is the recommended production recipe: disable automatic migrations, then run `resources setup` as an
explicit deployment step. When a schema difference is detected at runtime but `AutoCreate` is `None`, Wolverine
now logs a warning telling you the storage is out of date instead of silently skipping the migration.
:::

## Programmatic Management

Especially in automated tests, you may want to programmatically rebuild or clear out all persisted
messages. Here's a sample of the functionality in Wolverine to do just that:

<!-- snippet: sample_programmatic_management_of_message_storage -->
<a id='snippet-sample_programmatic_management_of_message_storage'></a>
```cs
// IHost would be your application in a testing harness
public static async Task testing_setup_or_teardown(IHost host)
{
    // Programmatically apply any outstanding message store
    // database changes
    await host.SetupResources();

    // Teardown the database message storage
    await host.TeardownResources();

    // Clear out any database message storage
    // also tries to clear out any messages held
    // by message brokers connected to your Wolverine app
    await host.ResetResourceState();

    var store = host.Services.GetRequiredService<IMessageStore>();

    // Rebuild the database schema objects
    // and delete existing message data
    // This is good for testing
    await store.Admin.RebuildAsync();

    // Remove all persisted messages
    await store.Admin.ClearAllAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L22-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_programmatic_management_of_message_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
`RebuildAsync()` and `ClearAllAsync()` operate on **envelope storage only** — the incoming, outgoing,
and dead letter tables. They deliberately do not touch the tables owned by a
[database-backed queue transport](/guide/messaging/transports/postgresql), because those are transport
data rather than envelope storage, and the right scope is genuinely ambiguous per provider (SQL Server's
rate-limit table, for instance, is registered the same way but has to survive a reset).

If you want the whole Wolverine storage footprint wiped between integration test runs, use
`IHost.ClearAllWolverineStorageAsync()` instead — see
[Resetting All Wolverine Storage in Tests](/guide/testing.html#resetting-all-wolverine-storage-in-tests).
:::

## Building Storage on Startup

To have any missing database schema objects built as needed on application startup, just add this option:

<!-- snippet: sample_resource_setup_on_startup -->
<a id='snippet-sample_resource_setup_on_startup'></a>
```cs
// This is rebuilding the persistent storage database schema on startup
builder.Host.UseResourceSetupOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L68-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_resource_setup_on_startup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Command Line Management

Assuming that you are using [JasperFx](https://github.com/JasperFx/jasperfx) as your command line parser in your Wolverine application as
shown in this last line of a `Program` code file:

<!-- snippet: sample_using_jasperfx_for_command_line_parsing -->
<a id='snippet-sample_using_jasperfx_for_command_line_parsing'></a>
```cs
// Opt into using JasperFx for command parsing
await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L95-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_jasperfx_for_command_line_parsing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And you're using the message persistence from either the `WolverineFx.SqlServer` or `WolverineFx.Postgresql`
or `WolverineFx.Marten` Nugets installed in your application, you will have some extended command line options
that you can discover from typing `dotnet run -- help` at the command line at the root of your project:

```bash
The available commands are:

  Alias       Description
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  check-env   Execute all environment checks against the application
  codegen     Utilities for working with JasperFx.CodeGeneration and JasperFx.RuntimeCompiler
  db-apply    Applies all outstanding changes to the database(s) based on the current configuration
  db-assert   Assert that the existing database(s) matches the current configuration
  db-dump     Dumps the entire DDL for the configured Marten database
  db-list     List all the databases as configured for this application
  db-patch    Evaluates the current configuration against the database and writes a patch and drop file if there are
              any differences
  describe    Writes out a description of your running application to either the console or a file
  help        List all the available commands
  resources   Check, setup, or teardown stateful resources of this system
  run         Start and run this .Net application
  storage     Administer the envelope storage
```

There's admittedly some duplication here with different options coming from [JasperFx](https://github.com/JasperFx/jasperfx) itself, the [Weasel.CommandLine](https://weasel.jasperfx.net/cli/) library,
and the `storage` command from Wolverine itself. To build out the schema objects for [message persistence](/guide/durability/), you
can use this command to apply any outstanding database changes necessary to bring the database schema to the Wolverine configuration:

```bash
dotnet run -- db-apply
```

::: info
The `db-apply`, `db-assert`, `db-patch`, `db-dump`, and `db-list` commands come from Weasel. See the per-command references at:

- [`db-apply`](https://weasel.jasperfx.net/cli/db-apply.html) — apply all outstanding changes to the configured database(s)
- [`db-assert`](https://weasel.jasperfx.net/cli/db-assert.html) — assert the live schema matches the configuration (good for CI deploy gates)
- [`db-patch`](https://weasel.jasperfx.net/cli/db-patch.html) — emit a SQL patch + rollback file for pending changes
- [`db-dump`](https://weasel.jasperfx.net/cli/db-dump.html) — dump the full DDL for the configured database(s)
- [`db-list`](https://weasel.jasperfx.net/cli/db-list.html) — list configured databases
:::

> NOTE: See the [Exporting SQL Scripts](#exporting-sql-scripts) section down the page for details of applying migrations when integrating with Marten

or this option -- but just know that this will also clear out any existing message data:

```bash
dotnet run -- storage rebuild
```

or this option which will also attempt to create Marten database objects or any known Wolverine transport objects like
Rabbit MQ / Azure Service Bus / AWS SQS queues:

```bash
dotnet run -- resources setup
```

## Clearing Node Ownership

::: warning
Don't use this option in production if any nodes are currently running
:::

If you ever have a node crash and need to force any persisted, incoming or outgoing messages to be picked up 
by another node (this should be automatic anyway, but locks might persist and Wolverine might take a bit to recognize that a node has crashed),
you can release the ownership of messages of all persisted nodes by:

```bash
dotnet run -- storage release
```

## Deleting Message Data

At any time you can clear out any existing persisted message data with:

```bash
dotnet run -- storage clear
```

## Exporting SQL Scripts

If you just want to export the SQL to create the necessary database objects, you can use:

```bash
dotnet run -- db-dump export.sql
```
where `export.sql` should be a file name.

### Marten integration

When integrating with Marten, scripts must be generated separately for both Marten and Wolverine resources.  
Resources are separated into databases and can be listed as below:

```bash
dotnet run -- db-list
# ┌────────────────────────────────────────┬───────────────────────────┐
# │ DatabaseUri                            │ SubjectUri                │
# ├────────────────────────────────────────┼───────────────────────────┤
# │ postgresql://localhost/postgres/orders │ marten://store/           │
# │ postgresql://localhost/postgres        │ wolverine://messages/main │
# └────────────────────────────────────────┴───────────────────────────┘
```

Once you've identified the database, pass the `-d` parameter with the `SubjectUri` from the output above to the `db-dump` command:

```bash
dotnet run -- db-dump -d marten://store/ export_marten.sql
dotnet run -- db-dump -d wolverine://messages/main export_wolverine.sql
```

## Disabling All Persistence <Badge type="tip" text="3.6" />

Let's say that you want to use the command line tooling to generate OpenAPI documentation, but do so
without Wolverine being able to connect to any external databases (or transports, and you'll have to disable both for this to work).
You can now do that with the option shown below as part of an [Alba](https://jasperfx.github.io/alba) test:

<!-- snippet: sample_bootstrap_with_no_persistence -->
<a id='snippet-sample_bootstrap_with_no_persistence'></a>
```cs
using var host = await AlbaHost.For<WolverineWebApi.Program>(builder =>
{
    builder.ConfigureServices(services =>
    {
        // You probably have to do both
        services.DisableAllExternalWolverineTransports();
        services.DisableAllWolverineMessagePersistence();
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/bootstrap_with_no_persistence.cs#L14-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrap_with_no_persistence' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
