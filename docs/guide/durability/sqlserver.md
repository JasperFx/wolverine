# Sql Server Integration

Wolverine supports a Sql Server backed message persistence strategy and even a Sql Server backed messaging transport
option. To get started, add the `WolverineFx.SqlServer` dependency to your application:

```bash
dotnet add package WolverineFx.SqlServer
```

## Message Persistence

To enable Sql Server to serve as Wolverine's [transactional inbox and outbox](./), you just need to use the `WolverineOptions.PersistMessagesWithSqlServer()`
extension method as shown below in a sample (that also uses Entity Framework Core):

<!-- snippet: sample_registering_efcore_middleware -->
<a id='snippet-sample_registering_efcore_middleware'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // Setting up Sql Server-backed message storage
    // This requires a reference to Wolverine.SqlServer
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    // Set up Entity Framework Core as the support
    // for Wolverine's transactional middleware
    opts.UseEntityFrameworkCoreTransactions();
    
    // Enrolling all local queues into the
    // durable inbox/outbox processing
    opts.Policies.UseDurableLocalQueues();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/EFCoreSample/ItemService/Program.cs#L36-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_efcore_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Sql Server Messaging Transport

::: info
The Sql Server transport was originally conceived as a way to handle much more volume through the scheduled message
functionality of Wolverine over using local queues backed by the transactional inbox.
:::

The `WolverineFx.SqlServer` Nuget also contains a simple messaging transport that was mostly meant to be usable for teams
who want asynchronous queueing without introducing more specialized infrastructure. To enable this transport in your code,
use this option which *also* activates Sql Server backed message persistence:

<!-- snippet: sample_using_sql_server_transport -->
<a id='snippet-sample_using_sql_server_transport'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        var connectionString = context.Configuration.GetConnectionString("sqlserver");
        opts.UseSqlServerPersistenceAndTransport(connectionString, "myapp")
            
            // Tell Wolverine to build out all necessary queue or scheduled message
            // tables on demand as needed
            .AutoProvision()
            
            // Optional that may be helpful in testing, but probably bad
            // in production!
            .AutoPurgeOnStartup();

        // Use this extension method to create subscriber rules
        opts.PublishAllMessages().ToSqlServerQueue("outbound");

        // Use this to set up queue listeners
        opts.ListenToSqlServerQueue("inbound")
            
            .CircuitBreaker(cb =>
            {
                // fine tune the circuit breaker
                // policies here
            })
            
            // Optionally specify how many messages to 
            // fetch into the listener at any one time
            .MaximumMessagesToReceive(50);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/DocumentationSamples.cs#L12-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_sql_server_transport' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The Sql Server transport is strictly queue-based at this point. The queues are configured as durable by default, meaning
that they are utilizing the transactional inbox and outbox. The Sql Server queues can also be buffered:

<!-- snippet: sample_setting_sql_server_queue_to_buffered -->
<a id='snippet-sample_setting_sql_server_queue_to_buffered'></a>
```cs
opts.ListenToSqlServerQueue("sender").BufferedInMemory();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Transport/compliance_tests.cs#L65-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_sql_server_queue_to_buffered' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using this option just means that the Sql Server queues can be used for both sending or receiving with no integration 
with the transactional inbox or outbox. This is a little more performant, but less safe as messages could be
lost if held in memory when the application shuts down unexpectedly. 



