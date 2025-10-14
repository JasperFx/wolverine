# Migration Guide

## Key Changes in 5.0

5.0 had very few breaking changes in the public API, but some in "publinternals" types most users would never touch. The
biggest change in the internals is the replacement of the venerable [TPL DataFlow library](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) 
with the [System.Threading.Channels library](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
in every place that Wolverine uses in memory queueing. The only change this caused to the public API was the removal of
the option for direct configuration of the TPL DataFlow `ExecutionOptions`. Endpoint ordering and parallelization options
are unchanged otherwise in the public fluent interface for configuration. 

The `IntegrateWithWolverine()` syntax for ["ancillary stores"](/guide/durability/marten/ancillary-stores) changed to a [nested closure](https://martinfowler.com/dslCatalog/nestedClosure.html) syntax to be more consistent
with the syntax for the main [Marten](https://martendb.io) store. The [Wolverine managed distribution of Marten projections and subscriptions](/guide/durability/marten/distribution)
now applies to the ancillary stores as well. 

The new [Partitioned Sequential Messaging](/guide/messaging/partitioning) feature is a potentially huge step forward for
building a Wolverine system that can efficiently and resiliently handle concurrent access to sensitive resources.

The [Aggregate Handler Workflow](/guide/durability/marten/event-sourcing) feature with Marten now supports strong typed identifiers.

The declarative data access features with Marten (`[Aggregate]`, `[ReadAggregate]`, `[Entity]` or `[Document]`) can utilize
Marten batch querying for better efficiency when a handler or HTTP endpoint uses more than one declaration for data loading.

## Key Changes in 4.0

* Wolverine dropped all support for .NET 6/7
* The previous dependencies on Oakton, JasperFx.Core, and JasperFx.CodeGeneration were all combined into a single [JasperFx](https://github.com/jasperfx/jasperfx) library. There are shims for any method with "Oakton" in its name, but these are marked as `[Obsolete]`. You can pretty well do a find and replace for "Oakton" to "JasperFx". If your Oakton command classes live in a different project than the runnable application, add this to that project's `Properties/AssemblyInfo.cs` file:
  ```cs
  using JasperFx;

  [assembly: JasperFxAssembly]
  ```
  This attribute replaces the older Oakton assembly attribute:
  ```cs
  using Oakton;

  [assembly: OaktonCommandAssembly]
  ```
* Internally, the full "Critter Stack" is trying to use `Uri` values to identify databases when targeting multiple databases in either a modular monolith approach or with multi-tenancy
* Many of the internal dependencies like Marten or AWS SQS SDK Nugets were updated
* The signature of the Kafka `IKafkaEnvelopeMapper` changed somewhat to be more efficient in message serialization
* Wolverine now supports [multi-tenancy through separate databases for EF Core](/guide/durability/efcore/multi-tenancy)
* The Open Telemetry span names for executing a message are now the [Wolverine message type name](/guide/messages.html#message-type-name-or-alias)

## Key Changes in 3.0

The 3.0 release did not have any breaking changes to the public API, but does come with some significant internal
changes.

### Lamar Removal

::: tip
Lamar is more "forgiving" than the built in `ServiceProvider`. If after converting to Wolverine 3.0, you receive
messages from `ServiceProvider` about not being able to resolve this, that, or the other, just go back to Lamar with
the steps in this guide.
:::

The biggest change is that Wolverine is no longer directly coupled to the [Lamar IoC library](https://jasperfx.github.io/lamar) and
Wolverine will no longer automatically replace the built in `ServiceProvider` with Lamar. At this point it is theoretically
possible to use Wolverine with any IoC library that fully supports the ASP.Net Core DI conformance behavior, but Wolverine
has only been tested against the default `ServiceProvider` and Lamar IoC containers. 

Do be aware if moving to Wolverine 3.0 that Lamar is more forgiving than `ServiceProvider`, so there might be some hiccups
if you choose to forgo Lamar. See the [Configuration Guide](/guide/configuration) for more information. Lamar does still have a little more
robust support for the code generation abilities in Wolverine (Wolverine uses the IoC configuration to generate code to inline
dependency creation in a way that's more efficient than an IoC tool at runtime -- when it can).

::: tip
If you have any issues with Wolverine's code generation about your message handlers or HTTP endpoints after upgrading to Wolverine 3.0,
please open a GitHub issue with Wolverine, but just know that you can probably fall back to using Lamar as the IoC tool
to "fix" those issues with code generation planning.
:::

Wolverine 3.0 can now be bootstrapped with the `HostApplicationBuilder` or any standard .NET bootstrapping mechanism through
`IServiceCollection.AddWolverine()`. The limitation of having to use `IHostBuilder` is gone.

### Marten Integration

The Marten/Wolverine `IntegrateWithWolverine()` integration syntax changed from a *lot* of optional arguments to a single
call with a nested lambda registration like this:

<!-- snippet: sample_using_integrate_with_wolverine_with_multiple_options -->
<a id='snippet-sample_using_integrate_with_wolverine_with_multiple_options'></a>
```cs
services.AddMarten(opts =>
    {
        opts.Connection(Servers.PostgresConnectionString);
        opts.DisableNpgsqlLogging = true;
    })
    .IntegrateWithWolverine(w =>
    {
        w.MessageStorageSchemaName = "public";
        w.TransportSchemaName = "public";
    })
    .ApplyAllDatabaseChangesOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/DuplicateMessageSending/Program.cs#L50-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_integrate_with_wolverine_with_multiple_options' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

All Marten/Wolverine integration options are available by this one syntax call now, with the exception of event subscriptions.

### Wolverine.RabbitMq

The RabbitMq transport recieved a significant overhaul for 3.0.

#### RabbitMq Client v7

The RabbitMq .NET client has been updated to v7, bringing with it an internal rewrite to support async I/O and vastly improved memory usage & throughput. This version also supports OTEL out of the box.

::: warning
RabbitMq v7 is newly released. If you use another RabbitMQ wrapper/bus in your application, hold off on upgrading until it also supports v7.
:::

#### Conventional Routing Improvements
- Queue bindings can now be manually overridden on a per-message basis via `BindToExchange`, this is useful for scenarios where you wish to use conventional naming between different applications but need other exchange types apart from `FanOut`. This should make conventional routing the default usage in the majority of situations. See [Conventional Routing](/guide/messaging/transports/rabbitmq/conventional-routing) for more information.
- Conventional routing entity creation has been split between the sender and receive side. Previously the sender would generate all exchange and queue bindings, but now if the sender has no handlers for a specific message, the queues will not be created.

#### General RabbitMQ Improvements
- Added support for Headers exchange
- Queues now apply bindings instead of exchanges. This is an internal change and shouldn't result in any obvious differences for users.
- The configuration model has expanded flexibility with Queues now bindable to Exchanges, alongside the existing model of Exchanges binding to Queues.
- The previous `BindExchange()` syntax was renamed to `DeclareExchange()` to better reflect Rabbit MQ operations

### Sagas

Wolverine 3.0 added optimistic concurrency support to the stateful `Saga` support. This will potentially cause database
migrations for any Marten-backed `Saga` types as it will now require the numeric version storage.

### Leader Election

The leader election functionality in Wolverine has been largely rewritten and *should* eliminate the issues with poor 
behavior in clusters or local debugging time usage where nodes do not gracefully shut down. Internal testing has shown
a significant improvement in Wolverine's ability to detect node changes and rollover the leadership election.

### Wolverine.PostgresSql

The PostgreSQL transport option requires you to explicitly set the `transportSchema`, or Wolverine will fall through to
using `wolverine_queues` as the schema for the database backed queues. Wolverine will no longer use the envelope storage
schema for the queues.

### Wolverine.Http

For [Wolverine.Http usage](/guide/http/), the Wolverine 3.0 usage of the less capable `ServiceProvider` instead of the previously
mandated [Lamar](https://jasperfx.github.io/lamar) library necessitates the usage of this API to register necessary
services for Wolverine.HTTP in addition to adding the Wolverine endpoints:

<!-- snippet: sample_adding_http_services -->
<a id='snippet-sample_adding_http_services'></a>
```cs
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Necessary services for Wolverine HTTP
// And don't worry, if you forget this, Wolverine
// will assert this is missing on startup:(
builder.Services.AddWolverineHttp();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L35-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_http_services' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Also for Wolverine.Http users, the `[Document]` attribute behavior in the Marten integration is now "required by default."

### Azure Service Bus

The Azure Service Bus will now "sanitize" any queue/subscription names to be all lower case. This may impact your usage of
conventional routing. Please report any problems with this to GitHub.

### Messaging

The behavior of `IMessageBus.InvokeAsync<T>(message)` changed in 3.0 such that the `T` response **is not also published as a 
message** at the same time when the initial message is sent with request/response semantics. Wolverine has gone back and forth
in this behavior in its life, but at this point, the Wolverine thinks that this is the least confusing behavioral rule. 

You can selectively override this behavior and tell Wolverine to publish the response as a message no matter what
by using the new 3.0 `[AlwaysPublishResponse]` attribute like this:

<!-- snippet: sample_using_AlwaysPublishResponse -->
<a id='snippet-sample_using_alwayspublishresponse'></a>
```cs
public class CreateItemCommandHandler
{
    // Using this attribute will force Wolverine to also publish the ItemCreated event even if
    // this is called by IMessageBus.InvokeAsync<ItemCreated>()
    [AlwaysPublishResponse]
    public async Task<(ItemCreated, SecondItemCreated)> Handle(CreateItemCommand command, IDocumentSession session)
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Name = command.Name
        };

        session.Store(item);

        return (new ItemCreated(item.Id, item.Name), new SecondItemCreated(item.Id, item.Name));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Bugs/Bug_305_invoke_async_with_return_not_publishing_with_tuple_return_value.cs#L65-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_alwayspublishresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
