# Multi-Tenancy with Wolverine

Multi-tenancy -- serving multiple customers (tenants) from a single deployed application -- is a first class concern
in Wolverine. Whether you need tenant-specific databases, isolated message brokers, or simply need to propagate
a tenant identifier through your messaging pipeline, Wolverine has you covered across every layer of the stack.

This tutorial provides a holistic overview of how multi-tenancy works in Wolverine and ties together the detailed
reference documentation for each subsystem.

::: tip
For additional context and real-world examples, see Jeremy Miller's blog post
[Multi-Tenancy in the Critter Stack](https://jeremydmiller.com/2026/04/01/multi-tenancy-in-the-critter-stack/).
:::

## How Tenant IDs Flow Through Wolverine

At its core, Wolverine treats the **tenant id** as message-level metadata. When a message is published or sent,
Wolverine attaches the active tenant id to the message envelope. When that message is received by a handler,
the tenant id is extracted and made available to the entire processing pipeline -- including persistence middleware,
EF Core `DbContext` resolution, Marten session scoping, and cascading messages.

This means that once a tenant id is established at any entry point (HTTP request, incoming message, or explicit code),
it automatically propagates through the entire downstream workflow without any manual plumbing.

## Entry Points: Establishing the Tenant ID

### From HTTP Requests

Wolverine.HTTP provides several built-in strategies to detect the tenant id from incoming HTTP requests:

- **Request header** (e.g. `x-tenant-id`)
- **Claim** from the authenticated user
- **Query string** parameter
- **Route argument**
- **Subdomain**
- **Fallback/default** value

These strategies are composable, and you can implement custom detection via the `ITenantDetection` interface.

See [Multi-Tenancy and ASP.Net Core](/guide/http/multi-tenancy) for full configuration details.

### From Incoming Messages

When Wolverine receives a message from any transport (RabbitMQ, Azure Service Bus, SQS, etc.), the tenant id
is automatically extracted from the message envelope metadata. No additional configuration is needed -- the
tenant id is propagated automatically when messages are sent.

### Programmatically

You can explicitly set the tenant id when publishing or invoking messages:

```cs
// Option 1: Using DeliveryOptions
await bus.PublishAsync(new CreateOrder(orderId), new DeliveryOptions
{
    TenantId = "tenant-abc"
});

// Option 2: Using InvokeForTenantAsync
var result = await bus.InvokeForTenantAsync<OrderConfirmation>("tenant-abc", new CreateOrder(orderId));
```

See [Multi-Tenancy in Message Handlers](/guide/handlers/multi-tenancy) for more details.

## Accessing the Tenant ID in Handlers

Wolverine can inject the current tenant id directly into your handler methods via the `TenantId` value type:

```cs
public static void Handle(CreateOrder command, TenantId tenantId, ILogger logger)
{
    logger.LogInformation("Processing order for tenant {TenantId}", tenantId.Value);
}
```

Cascading messages automatically inherit the tenant id from the originating message, so downstream handlers
will receive the same tenant context without any extra work.

## Database-Per-Tenant Persistence

The most common multi-tenancy pattern is **database-per-tenant**, where each tenant's data lives in a separate
database. Wolverine supports this across all three persistence integrations:

### With Entity Framework Core

Wolverine manages the mapping between tenant ids and database connection strings, and automatically configures
each `DbContext` instance with the correct connection for the active tenant:

```cs
var builder = Host.CreateApplicationBuilder();
var configuration = builder.Configuration;

builder.UseWolverine(opts =>
{
    // A "main" database is always required for messaging infrastructure
    opts.PersistMessagesWithPostgresql(configuration.GetConnectionString("main"))
        .RegisterStaticTenants(tenants =>
        {
            tenants.Register("tenant1", configuration.GetConnectionString("tenant1"));
            tenants.Register("tenant2", configuration.GetConnectionString("tenant2"));
            tenants.Register("tenant3", configuration.GetConnectionString("tenant3"));
        });

    // Register your DbContext with Wolverine-managed multi-tenancy
    opts.Services.AddDbContextWithWolverineManagedMultiTenancy<AppDbContext>(
        (builder, connectionString, _) =>
        {
            builder.UseNpgsql(connectionString.Value);
        }, AutoCreate.CreateOrUpdate);
});
```

Key points about EF Core multi-tenancy:

- Wolverine creates a **separate transactional inbox and outbox** in each tenant database
- The **transactional middleware** is fully multi-tenant aware
- **Storage actions** and the `[Entity]` attribute respect the active tenant
- Both **PostgreSQL** and **SQL Server** are supported as backends
- You can register **multiple `DbContext` types** with multi-tenancy simultaneously

You can also combine EF Core multi-tenancy with Marten, letting Marten manage the tenant-to-database mapping
while EF Core rides along:

```cs
opts.Services.AddMarten(m =>
{
    m.MultiTenantedDatabases(x =>
    {
        x.AddSingleTenantDatabase(tenant1Conn, "red");
        x.AddSingleTenantDatabase(tenant2Conn, "blue");
        x.AddSingleTenantDatabase(tenant3Conn, "green");
    });
}).IntegrateWithWolverine(x =>
{
    x.MainDatabaseConnectionString = mainConn;
});

opts.Services.AddDbContextWithWolverineManagedMultiTenancyByDbDataSource<AppDbContext>(
    (builder, dataSource, _) =>
    {
        builder.UseNpgsql(dataSource);
    }, AutoCreate.CreateOrUpdate);
```

For using EF Core multi-tenancy outside of Wolverine handlers (e.g., in legacy MVC controllers),
use `IDbContextOutboxFactory`:

```cs
public async Task HandleAsync(CreateItem command, TenantId tenantId, CancellationToken ct)
{
    var outbox = await _factory.CreateForTenantAsync<AppDbContext>(tenantId.Value, ct);
    outbox.DbContext.Items.Add(new Item { Name = command.Name });
    await outbox.PublishAsync(new ItemCreated { Id = item.Id });
    await outbox.SaveChangesAndFlushMessagesAsync(ct);
}
```

See [Multi-Tenancy with EF Core](/guide/durability/efcore/multi-tenancy) for the full reference.

::: tip
For additional context on the EF Core multi-tenancy story, see the blog post
[Wolverine 4 is Bringing Multi-Tenancy to EF Core](https://jeremydmiller.com/2025/05/15/wolverine-4-is-bringing-multi-tenancy-to-ef-core/).
:::

### With Marten

Marten supports two multi-tenancy strategies:

- **Conjoined tenancy** -- all tenants share a single database, with automatic tenant discrimination in queries
- **Database-per-tenant** -- each tenant gets its own PostgreSQL database

When using database-per-tenant with Marten, Wolverine automatically manages inbox/outbox tables in each tenant
database alongside a master database for node coordination.

See [Multi-Tenancy and Marten](/guide/durability/marten/multi-tenancy) for details.

### With Polecat

Polecat provides similar multi-tenancy support for SQL Server, with both conjoined and database-per-tenant
strategies available.

See [Multi-Tenancy and Polecat](/guide/durability/polecat/multi-tenancy) for details.

## Message Broker Isolation Per Tenant

For scenarios requiring complete message isolation between tenants -- such as IoT systems where each tenant's
devices connect to separate broker infrastructure -- Wolverine supports **broker-per-tenant** routing.

### RabbitMQ

Route tenants to separate RabbitMQ virtual hosts (or entirely separate brokers):

```cs
builder.UseWolverine(opts =>
{
    opts.UseRabbitMq(new Uri(builder.Configuration.GetConnectionString("main")))
        .AutoProvision()
        .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
        .AddTenant("one", "vh1")       // virtual host per tenant
        .AddTenant("two", "vh2")
        .AddTenant("three", new Uri(connectionString));  // or a completely separate broker

    opts.ListenToRabbitQueue("incoming");
    opts.ListenToRabbitQueue("global-queue").GlobalListener();  // not tenant-routed
    opts.PublishMessage<GlobalEvent>().ToRabbitQueue("events").GlobalSender();
});
```

See [RabbitMQ Multi-Tenancy](/guide/messaging/transports/rabbitmq/multi-tenancy) for details.

### Azure Service Bus

Route tenants to separate Azure Service Bus namespaces:

```cs
builder.UseWolverine(opts =>
{
    opts.UseAzureServiceBus(defaultConnectionString)
        .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)
        .AddTenantByNamespace("one", namespace1)
        .AddTenantByConnectionString("two", connectionString2);

    opts.ListenToAzureServiceBusQueue("incoming");
});
```

See [Azure Service Bus Multi-Tenancy](/guide/messaging/transports/azureservicebus/multi-tenancy) for details.

### Tenant ID Behavior Policies

Both RabbitMQ and Azure Service Bus support three policies for handling messages with unrecognized tenant ids:

| Policy | Behavior |
|--------|----------|
| `TenantedIdBehavior.FallbackToDefault` | Falls back to the default broker |
| `TenantedIdBehavior.IgnoreUnknownTenants` | Silently ignores the message |
| `TenantedIdBehavior.TenantIdRequired` | Throws an exception |

Use `.GlobalListener()` and `.GlobalSender()` to designate endpoints that should operate independently
of tenant routing.

::: tip
For more on this feature, see the blog post
[Message Broker per Tenant with Wolverine](https://jeremydmiller.com/2024/12/02/message-broker-per-tenant-with-wolverine/).
:::

## Combining Multiple Tenancy Layers

In a real-world system, you'll typically combine several of these features together. For example:

1. **HTTP tenant detection** identifies the tenant from a request header
2. **EF Core or Marten** routes to the correct tenant database
3. **Outgoing messages** automatically carry the tenant id
4. **Message broker routing** sends messages through tenant-specific infrastructure
5. **Downstream handlers** receive the tenant id and connect to the correct database

Wolverine handles all of this plumbing automatically once configured. The key insight is that
**tenant id flows as message metadata** -- once established, it propagates through the entire
processing pipeline without manual intervention.

## Further Reading

| Topic | Documentation | Blog Post |
|-------|--------------|-----------|
| Handler multi-tenancy | [Multi-Tenancy in Handlers](/guide/handlers/multi-tenancy) | |
| HTTP tenant detection | [Multi-Tenancy and ASP.Net Core](/guide/http/multi-tenancy) | |
| EF Core multi-tenancy | [Multi-Tenancy with EF Core](/guide/durability/efcore/multi-tenancy) | [Wolverine 4 is Bringing Multi-Tenancy to EF Core](https://jeremydmiller.com/2025/05/15/wolverine-4-is-bringing-multi-tenancy-to-ef-core/) |
| Marten multi-tenancy | [Multi-Tenancy and Marten](/guide/durability/marten/multi-tenancy) | |
| Polecat multi-tenancy | [Multi-Tenancy and Polecat](/guide/durability/polecat/multi-tenancy) | |
| RabbitMQ broker isolation | [RabbitMQ Multi-Tenancy](/guide/messaging/transports/rabbitmq/multi-tenancy) | [Message Broker per Tenant](https://jeremydmiller.com/2024/12/02/message-broker-per-tenant-with-wolverine/) |
| Azure Service Bus isolation | [ASB Multi-Tenancy](/guide/messaging/transports/azureservicebus/multi-tenancy) | [Message Broker per Tenant](https://jeremydmiller.com/2024/12/02/message-broker-per-tenant-with-wolverine/) |
| Modular monolith multi-database | [Modular Monoliths](/tutorials/modular-monolith) | [Wolverine 5 and Modular Monoliths](https://jeremydmiller.com/2025/10/27/wolverine-5-and-modular-monoliths/) |
| Comprehensive overview | | [Multi-Tenancy in the Critter Stack](https://jeremydmiller.com/2026/04/01/multi-tenancy-in-the-critter-stack/) |
