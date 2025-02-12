# Managing Message Storage

::: info
Wolverine will automatically check for the existence of necessary database tables and functions to support the
configured message storage, and will also apply any necessary database changes to comply with the configuration automatically.
:::

Wolverine uses the [Oakton "Stateful Resource"](https://jasperfx.github.io/oakton/guide/host/resources.html) model for managing
infrastructure configuration at development or even deployment time for configured items like the database-backed message storage or
message broker queues.

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
        opts.AutoBuildMessageStorageOnStartup = false;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DisablingStorageConstruction.cs#L10-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_auto_build_envelope_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L21-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_programmatic_management_of_message_storage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Building Storage on Startup

To have any missing database schema objects built as needed on application startup, just add this option:

<!-- snippet: sample_resource_setup_on_startup -->
<a id='snippet-sample_resource_setup_on_startup'></a>
```cs
// This is rebuilding the persistent storage database schema on startup
builder.Host.UseResourceSetupOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L55-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_resource_setup_on_startup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Command Line Management

Assuming that you are using [Oakton](https://jasperfx.github.io/oakton) as your command line parser in your Wolverine application as
shown in this last line of a .NET 6/7 `Program` code file:

<!-- snippet: sample_using_jasperfx_for_command_line_parsing -->
<a id='snippet-sample_using_jasperfx_for_command_line_parsing'></a>
```cs
// Opt into using JasperFx for command parsing
await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L85-L90' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_jasperfx_for_command_line_parsing' title='Start of snippet'>anchor</a></sup>
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
  db-patch    Evaluates the current configuration against the database and writes a patch and drop file if there are
              any differences
  describe    Writes out a description of your running application to either the console or a file
  help        List all the available commands
  resources   Check, setup, or teardown stateful resources of this system
  run         Start and run this .Net application
  storage     Administer the envelope storage
```

There's admittedly some duplication here with different options coming from [Oakton](https://jasperfx.github.io/oakton) itself, the [Weasel.CommandLine](https://github.com/JasperFx/weasel) library,
and the `storage` command from Wolverine itself. To build out the schema objects for [message persistence](/guide/durability/), you
can use this command to apply any outstanding database changes necessary to bring the database schema to the Wolverine configuration:

```bash
dotnet run -- db-apply
```
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

## Disabling All Persistence <Badge type="tip" text="3.6" />

Let's say that you want to use the command line tooling to generate OpenAPI documentation, but do so
without Wolverine being able to connect to any external databases (or transports, and you'll have to disable both for this to work).
You can now do that with the option shown below as part of an [Alba](https://jasperfx.github.io/alba) test:

<!-- snippet: sample_bootstrap_with_no_persistence -->
<a id='snippet-sample_bootstrap_with_no_persistence'></a>
```cs
using var host = await AlbaHost.For<Program>(builder =>
{
    builder.ConfigureServices(services =>
    {
        // You probably have to do both
        services.DisableAllExternalWolverineTransports();
        services.DisableAllWolverineMessagePersistence();
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/bootstrap_with_no_persistence.cs#L14-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrap_with_no_persistence' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
