# Aspire Dashboard Integration

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

## Installation

Add the package to your Aspire **AppHost** project (not the Wolverine service itself):

```bash
dotnet add package JasperFx.Aspire
```

## On-demand command buttons

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

### Mutating verbs

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

## Surfacing Wolverine's own verbs

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

## Startup gates

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

## Relationship to Wolverine's command line

This is purely a presentation layer over the [Wolverine CLI](/guide/command-line): each button or gate
runs the very same verb you would run with `dotnet run -- <verb>`, in a short-lived child process of the
same application. Nothing about your Wolverine configuration changes — the only requirement is the
standard `RunJasperFxCommands(args)` bootstrap, which lets the app execute a verb and exit instead of
starting the long-running host.

## See also

- [Command Line Integration](/guide/command-line) — the full Wolverine CLI surface
- [Code Generation](/guide/codegen) — what `codegen preview`/`write` produce
- [JasperFx.Aspire reference](https://shared-libs.jasperfx.net/cli/aspire.html) — every option and the underlying mechanics
