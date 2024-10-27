# Multi-Tenancy and Marten

::: info
This functionality was a very late addition just in time for Wolverine 1.0.
:::

Wolverine.Marten fully supports Marten multi-tenancy features. Both ["conjoined" multi-tenanted documents](https://martendb.io/documents/multi-tenancy.html) and full blown
[multi-tenancy through separate databases](https://martendb.io/configuration/multitenancy.html).

Some important facts to know:

* Wolverine.Marten's transactional middleware is able to respect the [tenant id from Wolverine](/guide/handlers/multi-tenancy) in resolving an `IDocumentSession`
* If using a database per tenant(s) strategy with Marten, Wolverine.Marten is able to create separate message storage tables in each tenant Postgresql database
* With the strategy above though, you'll need a "master" PostgreSQL database for tenant neutral operations as well
* The 1.0 durability agent is happily able to work against both the master and all of the tenant databases for reliable messaging

## Database per Tenant

::: info
All of these samples are taken from the [MultiTenantedTodoWebService sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService);
:::

To get started using Wolverine with Marten's database per tenant strategy, configure Marten multi-tenancy as you normally
would, but you also need to specify a "master" database connection string for Wolverine as well as shown below:

<!-- snippet: sample_configuring_wolverine_for_marten_multi_tenancy -->
<a id='snippet-sample_configuring_wolverine_for_marten_multi_tenancy'></a>
```cs
// Adding Marten for persistence
builder.Services.AddMarten(m =>
    {
        // With multi-tenancy through a database per tenant
        m.MultiTenantedDatabases(tenancy =>
        {
            // You would probably be pulling the connection strings out of configuration,
            // but it's late in the afternoon and I'm being lazy building out this sample!
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant1;Username=postgres;password=postgres", "tenant1");
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant2;Username=postgres;password=postgres", "tenant2");
            tenancy.AddSingleTenantDatabase("Host=localhost;Port=5433;Database=tenant3;Username=postgres;password=postgres", "tenant3");
        });

        m.DatabaseSchemaName = "mttodo";
    })
    .IntegrateWithWolverine(x => x.MasterDatabaseConnectionString = connectionString);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Program.cs#L12-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_for_marten_multi_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And you'll probably want this as well to make sure the message storage is in all the databases upfront:

<!-- snippet: sample_add_resource_setup_on_startup -->
<a id='snippet-sample_add_resource_setup_on_startup'></a>
```cs
builder.Services.AddResourceSetupOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Program.cs#L33-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_add_resource_setup_on_startup' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Lastly, this is the Wolverine set up:

<!-- snippet: sample_wolverine_setup_for_marten_multitenancy -->
<a id='snippet-sample_wolverine_setup_for_marten_multitenancy'></a>
```cs
// Wolverine usage is required for WolverineFx.Http
builder.Host.UseWolverine(opts =>
{
    // This middleware will apply to the HTTP
    // endpoints as well
    opts.Policies.AutoApplyTransactions();

    // Setting up the outbox on all locally handled
    // background tasks
    opts.Policies.UseDurableLocalQueues();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Program.cs#L39-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverine_setup_for_marten_multitenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

From there, you should be completely ready to use Marten + Wolverine with usages like this:

<!-- snippet: sample_invoke_for_tenant -->
<a id='snippet-sample_invoke_for_tenant'></a>
```cs
// While this is still valid....
[WolverineDelete("/todoitems/{tenant}/longhand")]
public static async Task Delete(
    string tenant,
    DeleteTodo command,
    IMessageBus bus)
{
    // Invoke inline for the specified tenant
    await bus.InvokeForTenantAsync(tenant, command);
}

// Wolverine.HTTP 1.7 added multi-tenancy support so
// this short hand works without the extra jump through
// "Wolverine as Mediator"
[WolverineDelete("/todoitems/{tenant}")]
public static void Delete(
    DeleteTodo command, IDocumentSession session)
{
    // Just mark this document as deleted,
    // and Wolverine middleware takes care of the rest
    // including the multi-tenancy detection now
    session.Delete<Todo>(command.Id);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/MultiTenantedTodoService/MultiTenantedTodoService/Endpoints.cs#L74-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoke_for_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Conjoined Multi-Tenancy

First, let's try just "conjoined" multi-tenancy where there's still just one database for Marten. From the tests, here's
a simple Marten persisted document that requires the "conjoined" tenancy model, and a command/handler combination for 
inserting new documents with Marten:

<!-- snippet: sample_conjoined_multi_tenancy_sample_code -->
<a id='snippet-sample_conjoined_multi_tenancy_sample_code'></a>
```cs
// Implementing Marten's ITenanted interface
// also makes Marten treat this document type as
// having "conjoined" multi-tenancy
public class TenantedDocument : ITenanted
{
    public Guid Id { get; init; }

    public string TenantId { get; set; }
    public string Location { get; set; }
}

// A command to create a new document that's multi-tenanted
public record CreateTenantDocument(Guid Id, string Location);

// A message handler to create a new document. Notice there's
// absolutely NO code related to a tenant id, but yet it's
// fully respecting multi-tenancy here in a second
public static class CreateTenantDocumentHandler
{
    public static IMartenOp Handle(CreateTenantDocument command)
    {
        return MartenOps.Insert(new TenantedDocument{Id = command.Id, Location = command.Location});
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/MultiTenancy/conjoined_tenancy.cs#L87-L114' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conjoined_multi_tenancy_sample_code' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For completeness, here's the Wolverine and Marten bootstrapping:

<!-- snippet: sample_setup_with_conjoined_tenancy -->
<a id='snippet-sample_setup_with_conjoined_tenancy'></a>
```cs
_host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine()
            .UseLightweightSessions();

        opts.Policies.AutoApplyTransactions();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/MultiTenancy/conjoined_tenancy.cs#L19-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setup_with_conjoined_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and after that, the calls to [InvokeForTenantAsync()]() "just work" as you can see if you squint hard enough reading this test:

<!-- snippet: sample_using_conjoined_tenancy -->
<a id='snippet-sample_using_conjoined_tenancy'></a>
```cs
[Fact]
public async Task execute_with_tenancy()
{
    var id = Guid.NewGuid();

    await _host.ExecuteAndWaitAsync(c =>
        c.InvokeForTenantAsync("one", new CreateTenantDocument(id, "Andor")));

    await _host.ExecuteAndWaitAsync(c =>
        c.InvokeForTenantAsync("two", new CreateTenantDocument(id, "Tear")));

    await _host.ExecuteAndWaitAsync(c =>
        c.InvokeForTenantAsync("three", new CreateTenantDocument(id, "Illian")));

    var store = _host.Services.GetRequiredService<IDocumentStore>();

    // Check the first tenant
    using (var session = store.LightweightSession("one"))
    {
        var document = await session.LoadAsync<TenantedDocument>(id);
        document.Location.ShouldBe("Andor");
    }

    // Check the second tenant
    using (var session = store.LightweightSession("two"))
    {
        var document = await session.LoadAsync<TenantedDocument>(id);
        document.Location.ShouldBe("Tear");
    }

    // Check the third tenant
    using (var session = store.LightweightSession("three"))
    {
        var document = await session.LoadAsync<TenantedDocument>(id);
        document.Location.ShouldBe("Illian");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/MultiTenancy/conjoined_tenancy.cs#L44-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_conjoined_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->





