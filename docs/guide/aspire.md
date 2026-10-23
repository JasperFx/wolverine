# Aspire Integration

Let's say you're orchestrating your system with [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/), and
your AppHost declares a PostgreSQL database and a RabbitMQ broker and references both from your Wolverine service.
Aspire hands your service those resources as connection strings in configuration, under
`ConnectionStrings:<resource name>`. All you need to tell Wolverine is the *name* of each connection string, and
every Wolverine transport and every Wolverine message store has an overload to do exactly that.

There's a second half to the Aspire story, and that's surfacing Wolverine's command line verbs on the Aspire
dashboard. That's covered further down in [Dashboard Integration](#dashboard-integration).

## Connecting to Aspire Resources by Name <Badge type="tip" text="6.52" />

In the AppHost, you add the resources and reference them from your Wolverine service the normal Aspire way:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").AddDatabase("wolverinedb");
var rabbitmq = builder.AddRabbitMQ("rabbitmq");

builder.AddProject<Projects.MyService>("service")
    .WithReference(postgres)
    .WithReference(rabbitmq)
    .WaitFor(postgres)
    .WaitFor(rabbitmq);

builder.Build().Run();
```

And in the service itself, you just use the resource names:

```csharp
builder.UseWolverine(opts =>
{
    // ConnectionStrings:wolverinedb
    opts.PersistMessagesWithPostgresqlUsingNamedConnection("wolverinedb");

    // ConnectionStrings:rabbitmq
    opts.UseRabbitMqUsingNamedConnection("rabbitmq").AutoProvision();
});
```

There's no need to pull the connection strings out of `IConfiguration` yourself at bootstrapping time, and Wolverine
reads them at the right moment -- once the application's container exists, but before the message store is built or
any transport connects. That also holds for the `resources` command line verbs.

::: tip
The `...UsingNamedConnection()` methods aren't Aspire specific at all. Anything that puts a value at
`ConnectionStrings:<name>` -- `appsettings.json`, user secrets, environment variables like
`ConnectionStrings__rabbitmq` -- works the same way.
:::

### Transports

| Transport | Named connection | Aspire AppHost resource |
|---|---|---|
| [RabbitMQ](/guide/messaging/transports/rabbitmq/) | `UseRabbitMqUsingNamedConnection(name)` | `AddRabbitMQ(name)` |
| [Kafka](/guide/messaging/transports/kafka) | `UseKafkaUsingNamedConnection(name)` | `AddKafka(name)` |
| [Azure Service Bus](/guide/messaging/transports/azureservicebus/) | `UseAzureServiceBusUsingNamedConnection(name)` | `AddAzureServiceBus(name)` |
| [Amazon SQS](/guide/messaging/transports/sqs/) | `UseAmazonSqsTransportUsingNamedConnection(name)` | `AddLocalStack(name)` or `AddFlociAws(name)` (emulators only) |
| [Amazon SNS](/guide/messaging/transports/sns) | `UseAmazonSnsTransportUsingNamedConnection(name)` | `AddLocalStack(name)` or `AddFlociAws(name)` (emulators only) |
| [NATS](/guide/messaging/transports/nats) | `UseNatsUsingNamedConnection(name)` | `AddNats(name)` |
| [Redis](/guide/messaging/transports/redis) | `UseRedisTransportUsingNamedConnection(name)` | `AddRedis(name)` |
| [Pulsar](/guide/messaging/transports/pulsar) | `UsePulsarUsingNamedConnection(name)` | none yet |
| [MQTT](/guide/messaging/transports/mqtt) | `UseMqttUsingNamedConnection(name)` | `AddMosquitto(name)` (CommunityToolkit) |
| [GCP Pub/Sub](/guide/messaging/transports/gcp-pubsub/) | `UsePubsubUsingNamedConnection(projectId, name)` | `AddFlociGcp(name)` (emulator only) |

Every transport above that supports more than one broker also has an
`AddNamed{Transport}BrokerUsingNamedConnection(brokerName, connectionStringName)` method for applications talking
to more than one broker of the same kind.

### Message Stores

| Message store | Named connection | Data source from the container | Aspire AppHost resource |
|---|---|---|---|
| [PostgreSQL](/guide/durability/postgresql) | `PersistMessagesWithPostgresqlUsingNamedConnection(name)` | `PersistMessagesWithPostgresqlUsingDataSourceFromContainer()` | `AddPostgres("server").AddDatabase(name)` |
| [SQL Server](/guide/durability/sqlserver) | `PersistMessagesWithSqlServerUsingNamedConnection(name)` | n/a | `AddSqlServer("server").AddDatabase(name)` |
| [MySQL](/guide/durability/mysql) | `PersistMessagesWithMySqlUsingNamedConnection(name)` | `PersistMessagesWithMySqlUsingDataSourceFromContainer()` | `AddMySql("server").AddDatabase(name)` |
| [Oracle](/guide/durability/oracle) | `PersistMessagesWithOracleUsingNamedConnection(name)` | n/a | `AddOracle("server").AddDatabase(name)` |
| [SQLite](/guide/durability/sqlite) | `PersistMessagesWithSqliteUsingNamedConnection(name)` | n/a | `AddSqlite(name)` (CommunityToolkit) |

The "data source from the container" methods are for when you're using an Aspire *client* integration like
`builder.AddNpgsqlDataSource("wolverinedb")` in the service project, and you'd rather Wolverine use that very same
pooled data source. Wolverine does not dispose a data source it gets from the container.

The database-backed message stores can also resolve their static per-tenant databases by name:

```csharp
opts.PersistMessagesWithPostgresqlUsingNamedConnection("wolverinedb")
    .RegisterStaticTenantsUsingNamedConnections(tenants =>
    {
        // tenant id, then the name of its connection string
        tenants.Register("tenant1", "tenant1db");
        tenants.Register("tenant2", "tenant2db");
    });
```

::: info
The Marten, Polecat and Fisher integrations get their database from the document store through
`IntegrateWithWolverine()`, so there's nothing named to configure on the Wolverine side for those.
:::

### When a Name Is Missing

Honestly, the most common way to get this wrong is to forget the `.WithReference()` call in the AppHost. Wolverine
checks every named connection string at the very top of startup and fails *once*, naming every missing one and what
to add to the AppHost, rather than failing on the first one and making you redeploy to find the next:

```text
2 connection strings required by this Wolverine application are missing from configuration:
  * 'wolverinedb', required by the PostgreSQL message store
  * 'rabbitmq', required by the Rabbit MQ transport
 Configured connection strings: 'postgres'. If this application runs under .NET Aspire, add the resource in the
 AppHost (builder.AddPostgres("postgres").AddDatabase("wolverinedb"); builder.AddRabbitMQ("rabbitmq")) and
 reference it from this project with .WithReference(...). ...
```

That's thrown as a `MissingNamedConnectionStringsException`. The message only ever lists connection string *names*,
never their values, since those are credentials.

You can also see every named connection string, what needs it, and whether it resolves with the `describe` command:

```bash
dotnet run -- describe
```

The "Named Connection Strings" table comes first, and `describe` still shows it when a name is missing.

## Dashboard Integration

The [JasperFx.Aspire](https://www.nuget.org/packages/JasperFx.Aspire) package surfaces a Wolverine
application's [command line verbs](/guide/command-line) — `check-env`, `describe`, `codegen`,
`resources`, and your own — as **clickable custom commands** on the resource tile in the
[.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) dashboard, and can run provisioning
verbs as **startup gates** that finish before the service boots.

Wolverine applications get this for free: because your `Program.cs` ends in `RunJasperFxCommands(args)`
(see [Command Line Integration](/guide/command-line)), the app already answers every verb. JasperFx.Aspire
just wires those verbs to the dashboard — there is no Wolverine-specific code to write.

::: info
This page covers the Wolverine-relevant usage. The full `JasperFx.Aspire` reference — every option, the
child-process mechanics, and dynamic discovery — lives at
[shared-libs.jasperfx.net/cli/aspire](https://shared-libs.jasperfx.net/cli/aspire.html).
:::

### Installation

Add the package to your Aspire **AppHost** project (not the Wolverine service itself):

```bash
dotnet add package JasperFx.Aspire
```

### On-demand command buttons

Call `WithJasperFxCommands()` on the project resource in your AppHost:

```cs
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands();

builder.Build().Run();
```

By default this adds the **read-only** verbs, enabled while the resource is running, with no
confirmation prompt:

| Button                     | Verb              | What it does for a Wolverine app                                   |
|----------------------------|-------------------|--------------------------------------------------------------------|
| **Check environment**      | `check-env`       | Runs the app's environment checks.                                 |
| **Describe**               | `describe`        | Dumps Wolverine's configuration — listeners, message routing, sending endpoints, error handling, HTTP endpoints — into the logs. |
| **Preview generated code** | `codegen preview` | Previews the handler/runtime code Wolverine generates (read-only). |

#### Mutating verbs

The verbs that change state are opt-in and prompt for confirmation. Enable them with
`IncludeMutatingCommands`:

```cs
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands(opts =>
    {
        opts.IncludeMutatingCommands = true; // adds: Apply resources, Write generated code
    });
```

| Button                   | Verb            | What it does for a Wolverine app                                               |
|--------------------------|-----------------|---------------------------------------------------------------------------------|
| **Apply resources**      | `resources setup` | Provisions Wolverine's stateful infrastructure — message broker queues/exchanges/topics and the durable inbox/outbox database tables. |
| **Write generated code** | `codegen write` | Generates Wolverine's handler/runtime code ahead of time and writes it to disk. |

### Surfacing Wolverine's own verbs

Beyond the standard verbs above, Wolverine registers its own commands (e.g. `storage` to administer
the durable message storage, and `wolverine-diagnostics`). Two ways to put those on the dashboard:

Add a single verb explicitly:

```cs
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands()
    .WithJasperFxCommand("storage", "counts"); // a "storage counts" button
```

…or turn on **dynamic discovery**, which asks the running build of the app what verbs it actually has
(via `help --json`) and renders a button per verb — picking up `storage`, `wolverine-diagnostics`, and
any [custom commands](https://shared-libs.jasperfx.net/cli/writing-commands.html) automatically:

```cs
builder.AddProject<Projects.Api>("api")
    .WithJasperFxCommands(opts => opts.DiscoverCommands = true);
```

Discovery is best-effort and falls back to the curated list if the project isn't built or discovery
fails. See the [reference docs](https://shared-libs.jasperfx.net/cli/aspire.html#dynamic-command-discovery)
for details.

### Startup gates

The buttons above run against a service that is *already running*. The complement is to run a
provisioning verb **before** the service starts, wired via Aspire's `WaitForCompletion`. For Wolverine
this is especially useful to:

- **Pre-generate handler code** (`codegen write`) so the first message doesn't pay the runtime
  code-generation/JIT cost.
- **Provision transports and message storage** (`resources setup`) so the queues/exchanges and the
  durable inbox/outbox tables exist before the app takes its first message.

```cs
var messaging = builder.AddRabbitMQ("rabbit");
var db = builder.AddPostgres("pg").AddDatabase("appdb");

builder.AddProject<Projects.Api>("api")
    .WithReference(messaging)
    .WithReference(db)
    .WaitFor(messaging)
    .WaitFor(db)
    .WithJasperFxStartup(c =>
    {
        c.Run("resources", "setup"); // provision transports + inbox/outbox before start
        c.Run("codegen", "write");   // pre-generate handlers — no first-request codegen
    });
```

Each gate is a first-class Aspire resource pointing at the **same project** with the verb as
arguments, so Aspire injects connection strings/environment natively. Declare `WithReference`/`WaitFor`
on the service **before** `WithJasperFxStartup` so each gate inherits them. A gate that exits non-zero
**blocks the service from starting** (fail fast) — you never start Wolverine against un-provisioned
infrastructure. Gates run sequentially in declaration order unless marked `Parallel`.

### Relationship to Wolverine's command line

This is purely a presentation layer over the [Wolverine CLI](/guide/command-line): each button or gate
runs the very same verb you would run with `dotnet run -- <verb>`, in a short-lived child process of the
same application. Nothing about your Wolverine configuration changes — the only requirement is the
standard `RunJasperFxCommands(args)` bootstrap, which lets the app execute a verb and exit instead of
starting the long-running host.

### See also

- [Command Line Integration](/guide/command-line) — the full Wolverine CLI surface
- [Code Generation](/guide/codegen) — what `codegen preview`/`write` produce
- [JasperFx.Aspire reference](https://shared-libs.jasperfx.net/cli/aspire.html) — every option and the underlying mechanics
