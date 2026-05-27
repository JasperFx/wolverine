# Diagnosing a Wolverine Application from the Command Line

Wolverine is a configuration-heavy framework. Conventions, policies, middleware, transports, and explicit
routing all layer together, which is powerful — but it can leave you asking "what is my app *actually*
doing?" This tutorial is a guided tour of the command-line tools Wolverine gives you to answer exactly
that, roughly in the order you'd reach for them when something looks off.

For the exhaustive flag-by-flag reference, see the [Command Line Integration](/guide/command-line) and
[Diagnostics](/guide/diagnostics) guides. This page is the walkthrough.

## Opt Into the Command Line

Every command below rides on the JasperFx command-line integration. You opt in with a single line at the
bottom of `Program.cs`:

```cs
// Opt into JasperFx for command line parsing to unlock the built in
// diagnostics and utility tools within your Wolverine application
return await app.RunJasperFxCommands(args);
```

From your project root, list what's available:

```bash
dotnet run -- help
```

::: tip
The metadata commands — `describe`, `codegen`, `codegen-preview`, `describe-routing`, and OpenAPI
generation — run **without a live database or message broker**. Wolverine detects "CLI metadata mode" and
stubs out persistence and transports, so these are safe to run in CI or on a laptop with no infrastructure.
:::

## Step 1: Get the Big Picture with `describe`

When in doubt, start here:

```bash
dotnet run -- describe
```

`describe` prints a series of tabular reports about the running configuration:

- **Wolverine Options** — the basics, including which assembly Wolverine thinks is your application
  assembly and which extensions loaded
- **Listeners** — every configured listening endpoint and local queue, with how each is configured
- **Message Routing** — where known, published message types are routed
- **Sending Endpoints** — configured endpoints that send messages externally
- **Error Handling** — a preview of the active message-failure policies
- **HTTP Endpoints** — all Wolverine HTTP endpoints (only when `WolverineFx.Http` is in use)

This is also the report the Wolverine team will most often ask you to paste when you need help.

## Step 2: See the Code Wolverine Generates

Wolverine generates the adapter code around your handlers and HTTP endpoints at startup. When you want to
*see* it — what middleware ran, how dependencies resolve, where transactions wrap — write it out or preview
it:

```bash
# Write all generated code to /Internal/Generated
dotnet run -- codegen write

# Or just dump it to the terminal
dotnet run -- codegen preview
```

`codegen` covers the whole app, which can be noisy. When you want to understand a **single** entry point,
reach for `codegen-preview` under the `wolverine-diagnostics` parent command <Badge type="tip" text="5.14" />:

```bash
# A message handler (fully-qualified, short, or handler class name — fuzzy matched)
dotnet run -- wolverine-diagnostics codegen-preview --handler CreateOrder

# An HTTP endpoint (requires Wolverine.Http; format "METHOD /path")
dotnet run -- wolverine-diagnostics codegen-preview --route "POST /api/orders"

# A proto-first gRPC service (requires Wolverine.Grpc)
dotnet run -- wolverine-diagnostics codegen-preview --grpc Greeter
```

The output is identical to `codegen preview`, but scoped to one handler, so the signal-to-noise ratio is
far higher.

## Step 3: Understand *Where* and *Why* Messages Route

`describe` shows you the routing table. When you need to focus on one message type — or understand *why* it
routes the way it does — use `describe-routing` <Badge type="tip" text="5.15" />:

```bash
# One message type (full name, short name, or fuzzy match)
dotnet run -- wolverine-diagnostics describe-routing CreateOrder

# The complete routing topology
dotnet run -- wolverine-diagnostics describe-routing --all
```

For a single type you get the local handler, a routes table (destination, local vs. external,
Buffered/Durable/Inline mode, outbox enrollment, serializer, and how each route was resolved), and any
message-level attributes such as `[DeliverWithin]`.

The most useful flag is `--explain` <Badge type="tip" text="6.0" />, which walks Wolverine's route-source
chain in order and shows what each source produced and which **terminating** source short-circuited the rest:

```bash
dotnet run -- wolverine-diagnostics describe-routing CreateOrder --explain

# Same explanation as structured JSON, for tooling or AI agents
dotnet run -- wolverine-diagnostics describe-routing CreateOrder --json
```

This is the command-line surface over the `IWolverineRuntime.ExplainRoutingFor(Type)` API. The text output
is deliberately stable and labeled so it reads well for humans *and* parses cleanly for automated tooling.
See [Troubleshooting Message Routing](/guide/diagnostics#troubleshooting-message-routing) for the
programmatic side.

## Step 4: Check Your Infrastructure

Wolverine's transports and the durable inbox/outbox register self-diagnosing environment checks — can I
reach the database? the broker? are the IoC registrations valid?

```bash
dotnet run -- check-env
```

To create, inspect, or tear down the stateful infrastructure Wolverine needs (queues, tables, topics):

```bash
dotnet run -- resources setup     # also: check, list, teardown, statistics
```

`resources setup` is a great way to provision a clean environment before a test run.

## Step 5: Inspect and Recover Message Storage

For applications using the durable inbox/outbox, the `storage` command administers the message store:

```bash
dotnet run -- storage counts     # incoming / outgoing / scheduled / dead-letter / handled
dotnet run -- storage clear
dotnet run -- storage rebuild                                   # --file to emit the schema script
dotnet run -- storage release --exception-type Some.Exception   # replay dead-lettered messages
```

`storage counts` is the quick "is anything backing up?" check, and `release` re-queues dead-lettered
envelopes (optionally filtered to a single exception type). To purge inbox rows already marked `Handled`:

```bash
dotnet run -- clear-handled
```

## Step 6: Export a Full Snapshot with `capabilities`

<Badge type="tip" text="5.8" />

```bash
dotnet run -- capabilities wolverine.json
```

Writes a complete JSON description of the application — settings, message types, message store, messaging
endpoints, even configured event stores. It's useful for support, for feeding external tooling, and for
detecting unintended configuration drift between deployments.

## Bonus: Generate OpenAPI Offline (Wolverine.Http)

If you use `WolverineFx.Http`, you can generate the OpenAPI document without starting the host — no database
or broker required, which makes it CI-friendly:

```bash
dotnet run -- openapi --list                  # list document names from AddOpenApi()
dotnet run -- openapi -d v1 -o swagger.json    # generate a document to a file
dotnet run -- openapi --route "GET /orders/{id}"
```

## Cheat Sheet

| Command | What it answers |
|---|---|
| `describe` | What's my whole configuration? |
| `codegen write` / `preview` | What code is Wolverine generating? |
| `wolverine-diagnostics codegen-preview` | …for *one* handler / endpoint / gRPC service |
| `wolverine-diagnostics describe-routing [--explain] [--json]` | Where — and *why* — does a message route? |
| `check-env` | Can I connect to my infrastructure? |
| `resources setup / check / teardown` | Provision or inspect stateful resources |
| `storage counts / clear / rebuild / release` | Inbox/outbox state and recovery |
| `clear-handled` | Purge handled inbox rows |
| `capabilities <file>.json` | Full machine-readable app snapshot |
| `openapi` | Generate OpenAPI docs offline (Wolverine.Http) |

## Where to Go Next

- [Command Line Integration](/guide/command-line) — the full reference, including every flag
- [Diagnostics](/guide/diagnostics) — handler-discovery troubleshooting and the routing-explanation API
- [Code Generation](/guide/codegen) — how Wolverine's generated code works
