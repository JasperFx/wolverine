# Multi-Tenancy and Polecat

Wolverine.Polecat fully supports Polecat multi-tenancy features, including both conjoined multi-tenanted documents and full blown
multi-tenancy through separate databases.

Some important facts to know:

* Wolverine.Polecat's transactional middleware is able to respect the [tenant id from Wolverine](/guide/handlers/multi-tenancy) in resolving an `IDocumentSession`
* If using a database per tenant(s) strategy with Polecat, Wolverine.Polecat is able to create separate message storage tables in each tenant SQL Server database
* With the strategy above, you'll need a "master" SQL Server database for tenant neutral operations as well
* The durability agent is able to work against both the master and all of the tenant databases for reliable messaging

## Database per Tenant

To get started using Wolverine with Polecat's database per tenant strategy, configure Polecat multi-tenancy as you normally
would, but you also need to specify a "master" database connection string for Wolverine:

```cs
builder.Services.AddPolecat(m =>
    {
        m.MultiTenantedDatabases(tenancy =>
        {
            tenancy.AddSingleTenantDatabase("Server=localhost;Database=tenant1;...", "tenant1");
            tenancy.AddSingleTenantDatabase("Server=localhost;Database=tenant2;...", "tenant2");
            tenancy.AddSingleTenantDatabase("Server=localhost;Database=tenant3;...", "tenant3");
        });
    })
    .IntegrateWithWolverine(x => x.MainDatabaseConnectionString = connectionString);
```

And you'll probably want this as well to make sure the message storage is in all the databases upfront:

```cs
builder.Services.AddResourceSetupOnStartup();
```

Lastly, this is the Wolverine set up:

```cs
builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
    opts.Policies.UseDurableLocalQueues();
});
```

From there, you should be ready to use Polecat + Wolverine with usages like:

```cs
[WolverineDelete("/todoitems/{tenant}")]
public static void Delete(
    DeleteTodo command, IDocumentSession session)
{
    session.Delete<Todo>(command.Id);
}
```

## Conjoined Multi-Tenancy

For "conjoined" multi-tenancy where there's still just one database:

```cs
public class TenantedDocument
{
    public Guid Id { get; init; }
    public string TenantId { get; set; }
    public string Location { get; set; }
}

public record CreateTenantDocument(Guid Id, string Location);

public static class CreateTenantDocumentHandler
{
    public static IPolecatOp Handle(CreateTenantDocument command)
    {
        return PolecatOps.Insert(new TenantedDocument{Id = command.Id, Location = command.Location});
    }
}
```

Bootstrapping:

```cs
_host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddPolecat(connectionString)
            .IntegrateWithWolverine();

        opts.Policies.AutoApplyTransactions();
    }).StartAsync();
```

And then the calls to `InvokeForTenantAsync()` just work:

```cs
await bus.InvokeForTenantAsync("one", new CreateTenantDocument(id, "Andor"));
await bus.InvokeForTenantAsync("two", new CreateTenantDocument(id, "Tear"));
```
