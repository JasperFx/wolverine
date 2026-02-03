# RavenDb Integration <Badge type="tip" text="3.0" />

Wolverine supports a [RavenDb](https://ravendb.net/) backed message persistence strategy
option as well as RavenDb-backed transactional middleware and saga persistence. To get started, add the `WolverineFx.RavenDb` dependency to your application:

```bash
dotnet add package WolverineFx.RavenDb
```

and in your application, tell Wolverine to use RavenDb for message persistence:

<!-- snippet: sample_bootstrapping_with_ravendb -->
<a id='snippet-sample_bootstrapping_with_ravendb'></a>
```cs
var builder = Host.CreateApplicationBuilder();

// You'll need a reference to RavenDB.DependencyInjection
// for this one
builder.Services.AddRavenDbDocStore(raven =>
{
    // configure your RavenDb connection here
});

builder.UseWolverine(opts =>
{
    // That's it, nothing more to see here
    opts.UseRavenDbPersistence();
    
    // The RavenDb integration supports basic transactional
    // middleware just fine
    opts.Policies.AutoApplyTransactions();
});

// continue with your bootstrapping...
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/RavenDbTests/DocumentationSamples.cs#L14-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_bootstrapping_with_ravendb' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Also see [RavenDb's own documentation](https://ravendb.net/docs/article-page/6.0/csharp/start/guides/aws-lambda/existing-project) for bootstrapping RavenDb inside of a .NET application. 

## Message Persistence

The [durable inbox and outbox](/guide/durability/) options in Wolverine are completely supported with 
RavenDb as the persistence mechanism. This includes scheduled execution (and retries), dead letter queue storage 
using the `DeadLetterMessage` collection, and the ability to replay designated messages in the dead letter queue
storage.

## Saga Persistence

The RavenDb integration can serve as a [Wolverine Saga persistence mechanism](/guide/durability/sagas). The only limitation is that
your `Saga` types can _only_ use strings as the identity for the `Saga`. 

<!-- snippet: sample_ravendb_saga -->
<a id='snippet-sample_ravendb_saga'></a>
```cs
public class Order : Saga
{
    // Just use this for the identity
    // of RavenDb backed sagas
    public string Id { get; set; }
    
    // Handle and Start methods...
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/RavenDbTests/DocumentationSamples.cs#L41-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ravendb_saga' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's nothing else to do, if RavenDb integration is applied to your Wolverine, it's going to kick in
for saga persistence as long as your `Saga` type has a string identity property.

## Transactional Middleware

::: warning
The RavenDb transactional middleware **only** supports the RavenDb `IAsyncDocumentSession` service
:::

The normal configuration options for transactional middleware in Wolverine apply to the RavenDb backend, so either
mark handlers explicitly with `[Transactional]` like so:

<!-- snippet: sample_using_transactional_with_raven -->
<a id='snippet-sample_using_transactional_with_raven'></a>
```cs
public class CreateDocCommandHandler
{
    [Transactional]
    public async Task Handle(CreateDocCommand message, IAsyncDocumentSession session)
    {
        await session.StoreAsync(new FakeDoc { Id = message.Id });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/RavenDbTests/DocumentationSamples.cs#L60-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_transactional_with_raven' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you choose to do this more conventionally (which folks do tend to use quite often):

```csharp
        builder.UseWolverine(opts =>
        {
            // That's it, nothing more to see here
            opts.UseRavenDbPersistence();
            
            // The RavenDb integration supports basic transactional
            // middleware just fine
            opts.Policies.AutoApplyTransactions();
        });
```

and the transactional middleware will kick in on any message handler or HTTP endpoint that uses
the RavenDb `IAsyncDocumentSession` like this handler signature:

<!-- snippet: sample_raven_using_handler_for_auto_transactions -->
<a id='snippet-sample_raven_using_handler_for_auto_transactions'></a>
```cs
public class AlternativeCreateDocCommandHandler
{
    // Auto transactions would kick in just because of the dependency
    // on IAsyncDocumentSession
    public async Task Handle(CreateDocCommand message, IAsyncDocumentSession session)
    {
        await session.StoreAsync(new FakeDoc { Id = message.Id });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/RavenDbTests/DocumentationSamples.cs#L73-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_raven_using_handler_for_auto_transactions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The transactional middleware will also be applied for any usage of the `RavenOps` [side effects](/guide/handlers/side-effects) model
for Wolverine's RavenDb integration:

<!-- snippet: sample_using_ravendb_side_effects -->
<a id='snippet-sample_using_ravendb_side_effects'></a>
```cs
public record RecordTeam(string Team, int Year);

public static class RecordTeamHandler
{
    public static IRavenDbOp Handle(RecordTeam command)
    {
        return RavenOps.Store(new Team { Id = command.Team, YearFounded = command.Year });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/RavenDbTests/transactional_middleware.cs#L50-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_ravendb_side_effects' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## System Control Queues

The RavenDb integration to Wolverine does not yet come with a built in database control queue
mechanism, so you will need to add that from external messaging brokers as in this example
using Azure Service Bus:

<!-- snippet: sample_enabling_azure_service_bus_control_queues -->
<a id='snippet-sample_enabling_azure_service_bus_control_queues'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    // Connect to the broker in the simplest possible way
    opts.UseAzureServiceBus(azureServiceBusConnectionString)
        .AutoProvision()
        
        // This enables Wolverine to use temporary Azure Service Bus
        // queues created at runtime for communication between
        // Wolverine nodes
        .EnableWolverineControlQueues();

});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L193-L216' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_azure_service_bus_control_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For local development, there is also an option to let Wolverine just use its TCP transport
as a control endpoint with this configuration option:

```csharp
WolverineOptions.UseTcpForControlEndpoint();
```

In the option above, Wolverine is just looking for an unused port, and assigning that found port
as the listener for the node being bootstrapped. 

## RavenOps Side Effects

The `RavenOps` static class can be used as a convenience for RavenDb integration with Wolverine:

<!-- snippet: sample_ravenops -->
<a id='snippet-sample_ravenops'></a>
```cs
/// <summary>
/// Side effect helper class for Wolverine's integration with RavenDb
/// </summary>
public static class RavenOps
{
    /// <summary>
    /// Store a new RavenDb document
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IRavenDbOp Store<T>(T document) => new StoreDoc<T>(document);

    /// <summary>
    /// Delete this document in RavenDb
    /// </summary>
    /// <param name="document"></param>
    /// <returns></returns>
    public static IRavenDbOp DeleteDocument(object document) => new DeleteByDoc(document);

    /// <summary>
    /// Delete a RavenDb document by its id
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public static IRavenDbOp DeleteById(string id) => new DeleteById(id);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/Wolverine.RavenDb/IRavenDbOp.cs#L36-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ravenops' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See the Wolverine [side effects](/guide/handlers/side-effects) model for more information.

This integration also includes full support for the [storage action side effects](/guide/handlers/side-effects.html#storage-side-effects)
model when using RavenDb with Wolverine. 

## Entity Attribute Loading

The RavenDb integration is able to completely support the [Entity attribute usage](/guide/handlers/persistence.html#automatically-loading-entities-to-method-parameters).





