# Command Line Integration

@[youtube](3C5bacH0akU)

With help from its [JasperFx](https://github.com/JasperFx) team mate [Oakton](https://jasperfx.github.io/oakton), Wolverine supports quite a few command line diagnostic and resource management
tools. To get started, apply Oakton as the command line parser in your applications as shown in the last line of code in this
sample application bootstrapping from Wolverine's [Getting Started](/tutorials/getting-started):

::: info
This page covers the Wolverine-specific CLI surface. The underlying JasperFx command-line library that backs `RunJasperFxCommands` — including how to [author your own commands](https://shared-libs.jasperfx.net/cli/writing-commands.html), [argument/flag handling](https://shared-libs.jasperfx.net/cli/arguments-flags.html), and [environment checks](https://shared-libs.jasperfx.net/cli/environment-checks.html) — is documented at [shared-libs.jasperfx.net/cli](https://shared-libs.jasperfx.net/cli/). When a command accepts a generic flag not listed on this page, that's where to look first.
:::

<!-- snippet: sample_quickstart_program -->
<a id='snippet-sample_quickstart_program'></a>
```cs
using JasperFx;
using Quickstart;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// The almost inevitable inclusion of Swashbuckle:)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// For now, this is enough to integrate Wolverine into
// your application, but there'll be *many* more
// options later of course :-)
builder.Host.UseWolverine();

// Some in memory services for our application, the
// only thing that matters for now is that these are
// systems built by the application's IoC container
builder.Services.AddSingleton<UserRepository>();
builder.Services.AddSingleton<IssueRepository>();

var app = builder.Build();

// An endpoint to create a new issue that delegates to Wolverine as a mediator
app.MapPost("/issues/create", (CreateIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// An endpoint to assign an issue to an existing user that delegates to Wolverine as a mediator
app.MapPost("/issues/assign", (AssignIssue body, IMessageBus bus) => bus.InvokeAsync(body));

// Swashbuckle inclusion
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger"));

// Opt into using JasperFx for command line parsing
// to unlock built in diagnostics and utility tools within
// your Wolverine application
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Quickstart/Program.cs#L1-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_quickstart_program' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

From this project's root in the command line terminal tool of your choice, type:

```bash
dotnet run -- help
```

and you *should* get this hopefully helpful rundown of available command options:

```bash
The available commands are:
                                                                                                    
  Alias       Description                                                                           
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  check-env   Execute all environment checks against the application                                
  codegen     Utilities for working with JasperFx.CodeGeneration and JasperFx.RuntimeCompiler       
  describe    Writes out a description of your running application to either the console or a file  
  help        List all the available commands                                                       
  resources   Check, setup, or teardown stateful resources of this system                           
  run         Start and run this .Net application                                                   
  storage                Administer the Wolverine message storage
  wolverine-diagnostics  Wolverine diagnostics tools for inspecting generated code and runtime behavior


Use dotnet run -- ? [command name] or dotnet run -- help [command name] to see usage help about a specific command

```

## Describe a Wolverine Application

::: tip
While Wolverine certainly knows upfront what message types it handles, you may need to help Wolverine "know" what types
will be outgoing messages later with the [message discovery](/guide/messages.html#message-discovery) support.
:::

Wolverine is admittedly a configuration-heavy framework, and some combinations of conventions, policies, and explicit configuration
could easily lead to confusion about how the system is going to behave. To help ameliorate that possible situation -- but also to help the 
Wolverine team be able to remotely support folks using Wolverine -- you have this command line tool:

```bash
dotnet run -- describe
```

At this time, a Wolverine application will spit out command line reports about its configuration that
will describe:

* "Wolverine Options" - the basics properties as configured, including what Wolverine thinks is the application assembly and any registered extensions
* "Wolverine Listeners" - a tabular list of all the configured listening endpoints, including local queues, within the system and information about how they are configured
* "Wolverine Message Routing" - a tabular list of all the message routing for *known* messages published within the system
* "Wolverine Sending Endpoints" - a tabular list of all *known*, configured endpoints that send messages externally
* "Wolverine Error Handling" - a preview of the active message failure policies active within the system
* "Wolverine Http Endpoints" - shows all Wolverine HTTP endpoints. This is only active if WolverineFx.HTTP is used within the system

## Exporting System Capabilities <Badge type="tip" text="5.8" />

This command:

```bash
dotnet run capabilities wolverine.json
```

Will write a JSON file to "wolverine.json" that will completely describe all the configured settings, message types, message store,
messaging endpoints, and even event stores configured to this application. The Wolverine team may ask you for this file 
to help you troubleshoot issues in the future.

This functionality was originally built for consumption in the "CritterWatch" add on tool, but was requested by a [JasperFx Software](https://jasperfx.net)
client to provide a mechanism to detect any unintentional changes to Wolverine application configuration.

## CLI Commands Work Without External Connectivity

::: tip
This applies to `codegen write`, `codegen preview`, `describe`, and OpenAPI generation tools such as
`GetDocument.Insider` (Microsoft.Extensions.ApiDescription.Server). You do **not** need a running
database or message broker for these commands to succeed.
:::

Wolverine automatically detects when it is running in a metadata-only CLI mode and suppresses
persistence and transport initialization. No database connections or message broker connections
are opened. This allows commands like `codegen` and `describe` to work safely in CI pipelines or
developer machines that do not have external infrastructure available.

Detection is based on two signals:

1. **`DynamicCodeBuilder.WithinCodegenCommand`** — set by JasperFx when the `codegen` command is
   used, either via `dotnet run -- codegen ...` or the `--start` flag.
2. **`ASPNETCORE_HOSTINGSTARTUPASSEMBLIES` environment variable** — contains `"GetDocument"` when
   OpenAPI generation tools like `GetDocument.Insider` start the host.

When either condition is true, Wolverine applies the equivalent of "lightweight mode":
external transports are stubbed out, durability agents are disabled, and the durability
mode is set to `MediatorOnly`.

If you need to explicitly disable persistence initialization for other tooling (e.g., your own
OpenAPI generation pipeline), you can use the `DisableAllWolverineMessagePersistence()` extension:

```csharp
// In Program.cs or Startup.cs, guard with an environment check for your tooling
builder.Services.DisableAllWolverineMessagePersistence();
```

## Wolverine Diagnostics Commands <Badge type="tip" text="5.14" />

The `wolverine-diagnostics` command is an extensible parent command for deeper Wolverine-specific
inspection tools.

::: tip
Both `codegen-preview` and `describe-routing` work without database or message-broker connectivity.
Wolverine automatically detects CLI codegen mode and stubs out persistence and transports.
:::

### codegen-preview

Preview the full generated adapter code for a **specific** message handler, HTTP endpoint, or
proto-first gRPC service without generating all handlers at once. This is useful when you want to
understand exactly what middleware, dependency resolution, or transaction wrapping Wolverine
applies to a single entry point.

**Preview a message handler** (accepts fully-qualified name, short class name, or handler class name):

```bash
# Fully-qualified message type
dotnet run -- wolverine-diagnostics codegen-preview --handler MyApp.Orders.CreateOrder

# Short message type name (fuzzy match)
dotnet run -- wolverine-diagnostics codegen-preview --handler CreateOrder

# Handler class name
dotnet run -- wolverine-diagnostics codegen-preview --handler CreateOrderHandler
```

**Preview an HTTP endpoint** (requires Wolverine.HTTP; format: `"METHOD /path"`):

```bash
dotnet run -- wolverine-diagnostics codegen-preview --route "POST /api/orders"
dotnet run -- wolverine-diagnostics codegen-preview --route "GET /api/orders/{id}"
```

**Preview a proto-first gRPC service wrapper** (requires Wolverine.Grpc; accepts the proto service
name, the stub class name, or the generated file name):

```bash
# Bare proto service name (as it appears in the .proto file)
dotnet run -- wolverine-diagnostics codegen-preview --grpc Greeter

# Stub class name
dotnet run -- wolverine-diagnostics codegen-preview --grpc GreeterGrpcService

# Short alias
dotnet run -- wolverine-diagnostics codegen-preview -g Greeter
```

The output includes the full generated class — the `Handle` or `HandleAsync` override, all
middleware calls in order, dependency resolution from the IoC container, and any
transaction-wrapping frames. This is identical to what `codegen preview` outputs, but scoped to
exactly one handler so the signal-to-noise ratio is much higher.

### describe-routing <Badge type="tip" text="5.15" />

Inspect the message routing configuration for a specific message type or show a complete view of
all message routing in your application.

**Inspect routing for a single message type** (accepts full name, short name, or fuzzy match):

```bash
# Short class name
dotnet run -- wolverine-diagnostics describe-routing CreateOrder

# Fully-qualified name
dotnet run -- wolverine-diagnostics describe-routing MyApp.Orders.CreateOrder
```

The output for a single message type includes:

- **Local handler** — the handler class and method, if any
- **Routes table** — each destination with its type (local vs. external), endpoint mode
  (Buffered/Durable/Inline), outbox enrollment, serialization format, and how the route was
  resolved (local handler convention, explicit publish rule, transport routing convention, or
  `[LocalQueue]` attribute)
- **Message-level attributes** — any `ModifyEnvelopeAttribute`-derived attributes (e.g.,
  `[DeliverWithin]`) applied to the message class

**Show the complete routing topology** (all message types):

```bash
dotnet run -- wolverine-diagnostics describe-routing --all
```

The `--all` output includes:

- **Routing Conventions** — transport-level conventions registered via `RouteWith()`
- **Message Routing** table — every known message type with its destinations, mode, outbox status,
  and serializer; unrouted types are flagged in yellow
- **Listeners** — all configured listening endpoints with name, mode, and parallelism
- **Senders** — all configured sending endpoints with name, mode, and subscription count

## Other Highlights

* See the [code generation support](./codegen)
* The `storage` command helps manage the [durable messaging support](./durability/)
* Wolverine has direct support for [Oakton](https://jasperfx.github.io/oakton) environment checks and resource management that
  can be very helpful for Wolverine integrations with message brokers or database servers





