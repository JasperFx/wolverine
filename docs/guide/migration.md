# Migration Guide

## Key Changes in 6.0

::: warning
6.0 is currently in active development on the `main` branch. Items below describe the
state of `main` as the 6.0 work proceeds — the released 6.0 may add to or refine this
list. Bug fixes for the 5.x line continue to ship from the [`5.0` maintenance branch](https://github.com/JasperFx/wolverine/tree/5.0).
:::

### At a glance: settings & APIs that changed in 6.0

The table below is the **complete inventory** of changed defaults, removed APIs, and moved namespaces in 6.0. Anything not in this table is either backward-compatible (new property added with a sensible default) or an internal change with no observable surface. Each row links into the long-form explanation below; the `Migration action` column is the one-line summary if you only have time to skim.

| Setting / API | 5.x | 6.0 | Migration action |
|---|---|---|---|
| `WolverineOptions.ServiceLocationPolicy` | `AllowedButWarn` | **`NotAllowed`** *(BREAKING)* | Restructure registrations or [allow-list per type](#servicelocationpolicy-notallowed-is-the-default-breaking); or call [`opts.RestoreV5Defaults()`](#one-line-revert-restorev5defaults) |
| `WolverineOptions.UseNewtonsoftForSerialization(...)` | instance method on core `WolverineFx` | extension method in `WolverineFx.Newtonsoft` *(BREAKING)* | Install `WolverineFx.Newtonsoft`; add `using Wolverine.Newtonsoft;` |
| `IEndpointConfiguration<T>.CustomNewtonsoftJsonSerialization(...)` | instance method on the interface | extension method in `WolverineFx.Newtonsoft` *(BREAKING)* | Same |
| `IMassTransitInterop.UseNewtonsoftForSerialization(...)` | interface method | extension method in `WolverineFx.Newtonsoft` *(BREAKING)* | Same |
| `NewtonsoftSerializer` type | `Wolverine.Runtime.Serialization` namespace | `Wolverine.Newtonsoft` namespace *(BREAKING)* | Update `using` |
| `Subscription.Scope` JSON converter attribute | `[StringEnumConverter]` (Newtonsoft) | `[JsonStringEnumConverter]` (STJ) | Wire format unchanged; if you serialize `Subscription` yourself with Newtonsoft and rely on string-named scopes, configure `StringEnumConverter` on your own settings |
| `WolverineHttpOptions.UseNewtonsoftJsonForSerialization(...)` | instance method on core `WolverineFx.Http` | extension method in `WolverineFx.Http.Newtonsoft` *(BREAKING)* | Install `WolverineFx.Http.Newtonsoft`; call `services.AddWolverineHttpNewtonsoft();` alongside `AddWolverineHttp()`; add `using Wolverine.Http.Newtonsoft;` |
| `NewtonsoftHttpSerialization` type | `Wolverine.Http` namespace in core `WolverineFx.Http` | `Wolverine.Http.Newtonsoft` namespace in `WolverineFx.Http.Newtonsoft` *(BREAKING)* | Update `using` |
| `ProblemDetailsContinuationPolicy.WriteProblems` diagnostic logger dump | `JsonConvert.SerializeObject` (Newtonsoft) | `JsonSerializer.Serialize` (STJ) | None — diagnostic-only log line; output shape unchanged |
| `SnapshotLifecycle` namespace | `Marten.Events.Projections` | `JasperFx.Events.Projections` *(BREAKING)* | Add `using JasperFx.Events.Projections;` |
| `OperationRole` namespace | `Marten.Internal.Operations` | `Weasel.Core` *(BREAKING)* | Add `using Weasel.Core;` |
| Target framework | `net8.0;net9.0;net10.0` | `net9.0;net10.0` *(BREAKING)* | Move to .NET 9+ or pin Wolverine 5.x |
| Critter-stack package versions | 1.x line | 2.0-alpha line | Bump in lockstep across JasperFx, Marten, Polecat — full table below |
| `IForwardsTo<T>` discovery | implicit assembly scan at startup | **explicit `opts.RegisterMessageForwarder<TFrom, TTo>()`** *(BREAKING)* | Register each forwarder explicitly; or temporarily call [`opts.UseAutomaticForwarderDiscovery()`](#iforwardsto-discovery-is-now-explicit-breaking) (`[Obsolete]`, removed in 7.0) |
| `MartenConfigurationExpression.EventForwardingToWolverine(...)` | extension method on the Marten configuration expression *(both overloads `[Obsolete]` since 3.x)* | **removed** *(BREAKING)* | Move the flag into the `IntegrateWithWolverine` callback: [`IntegrateWithWolverine(x => x.UseFastEventForwarding = true)`](#eventforwardingtowolverine-removed-breaking) |
| `PulsarEndpoint.UriFor(...)` | static helpers on `PulsarEndpoint` *(both overloads `[Obsolete]`)* | **removed** *(BREAKING)* | For the Wolverine-URI form, call [`PulsarEndpointUri.Topic(...)`](#pulsarendpoint-urifor-removed-breaking); the native-topic-path form (`persistent://...`) was an internal-only seam now covered by `PulsarEndpoint.NativeTopicPath` |
| `Saga.Version` property type | `int` | `long` | Typically none — `int` widens implicitly to `long`. Only affects code that stores `saga.Version` in an `int` variable, casts it explicitly, or binds it to a column typed `int`. Tracks the matching `IRevisioned.Version` widening in Marten 9.0. |
| **One-line full revert** | n/a | [`opts.RestoreV5Defaults()`](#one-line-revert-restorev5defaults) | Flip every runtime default this method covers back to its 5.x value |

::: tip
If you just want to adopt the 6.0 API surface and bug fixes without simultaneously adopting the new runtime defaults, call [`opts.RestoreV5Defaults()`](#one-line-revert-restorev5defaults) once at the top of your `UseWolverine` lambda. Then opt into each new default at your own pace.
:::

---

### `.NET 8.0` support dropped (BREAKING)

Wolverine 6.0 requires **.NET 9.0 or .NET 10.0**. The JasperFx 2.0-alpha line that the rest of 6.0 builds on no longer targets net8.0, so neither does Wolverine.

If you're on .NET 8 LTS and aren't ready to move:

- Pin to the latest Wolverine 5.x release.
- Bug fixes for the 5.x line continue to ship from the [`5.0` maintenance branch](https://github.com/JasperFx/wolverine/tree/5.0) — open issues / PRs that target that branch the same way you do for `main`.

### Critter-stack 2.0-alpha dependency line (BREAKING)

The whole critter-stack moved to a coordinated 2.0-alpha set of packages built on JasperFx 2.0. Wolverine 6.0 pins:

| Package | 5.x line | 6.0 |
|---|---|---|
| JasperFx | 1.31.x | 2.0.0-alpha.10 |
| JasperFx.Events | 1.36.x | 2.0.0-alpha.3 |
| JasperFx.RuntimeCompiler | 4.5.x | 2.0.0-alpha.2 |
| JasperFx.SourceGeneration | 1.1.x | 2.0.0-alpha.2 |
| Marten | 8.35.x | 9.0.0-alpha.1 |
| Marten.AspNetCore | 8.35.x | 9.0.0-alpha.1 |
| Polecat | 2.1.x | 4.0.0-alpha.1 |

If you reference any of these directly from your own project, bump in lockstep with Wolverine.

### `SnapshotLifecycle` moved to `JasperFx.Events.Projections` (BREAKING)

`Marten.Events.Projections.SnapshotLifecycle` no longer exists in Marten 9. The same type now lives at `JasperFx.Events.Projections.SnapshotLifecycle` and is consumed by both Marten and Polecat.

If you import `using Marten.Events.Projections;` and reference `SnapshotLifecycle.Inline` / `SnapshotLifecycle.Async`, add `using JasperFx.Events.Projections;` alongside it. Marten's `Snapshot<T>(...)` overload still accepts the type from this new namespace.

There is no Wolverine documentation page for `SnapshotLifecycle` itself — it's a Marten projection-configuration type. The Marten 9 projection docs cover its full semantics; the only Wolverine-visible change is the `using` directive.

### `OperationRole` moved to `Weasel.Core` (BREAKING)

If you write custom `IStorageOperation` implementations (rare but supported), `OperationRole` now lives in `Weasel.Core` instead of `Marten.Internal.Operations`. Add `using Weasel.Core;`.

No Wolverine documentation page covers this directly — `IStorageOperation` is Marten internals territory. The only Wolverine-visible change is the `using` directive.

### `ServiceLocationPolicy.NotAllowed` is the default (BREAKING)

In Wolverine 6.0, `WolverineOptions.ServiceLocationPolicy` defaults to `NotAllowed` (was `AllowedButWarn` in 5.x). Apps that previously relied on Wolverine's code generation falling back to service location at runtime now throw `InvalidServiceLocationException` on startup.

**Preferred upgrade path** — change IoC registrations to forms Wolverine can see through:

- `AddScoped<TInterface, TImpl>()` instead of `AddScoped<TInterface>(sp => new TImpl(...))`
- Constructor injection into your handlers (Wolverine inlines that into generated code)

**Escape hatch** — opt specific types into the allow-list:

```csharp
opts.CodeGeneration.AlwaysUseServiceLocationFor<IMyOpaqueService>();
```

Use this for services with genuinely opaque lambda factory registrations (Refit proxies, EF Core `DbContext` types with runtime configuration, etc.). The rest of the codegen stays inlined; only the listed types route through the service locator.

**To prepare while still on 5.x:**

1. Run your app with `Warning`-level (or lower) logging on the `Wolverine` category. Look for log messages starting with `Utilizing service location for ...`.
2. For each one, either restructure the registration (preferred) or opt the type in explicitly with `opts.CodeGeneration.AlwaysUseServiceLocationFor<T>()`.
3. Once the warnings are gone, set `opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed` on 5.x to assert the absence going forward — on 6.0 this is the default, no explicit assignment needed.

See [Code Generation](/guide/codegen.html) for the full IoC + service-location story, including the LOUD callout at the top of that page.

### One-line revert: `RestoreV5Defaults()`

For applications that want to adopt Wolverine 6's API surface, NuGet line, target frameworks, and bug fixes — without simultaneously adopting its new runtime defaults — Wolverine 6.0 adds a one-line escape hatch:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.RestoreV5Defaults();
    // … the rest of your configuration
});
```

`RestoreV5Defaults()` flips every runtime default that changed between Wolverine 5.x and 6.x back to its 5.x value. Today that means **one** default: `ServiceLocationPolicy` flips from `NotAllowed` back to `AllowedButWarn`. As more defaults flip between 6.0 and 6.x patch releases, they get added to this method and to the [at-a-glance table above](#at-a-glance-settings-apis-that-changed-in-6-0).

**What this method does NOT do:**

- **It does not change the default JSON serializer.** System.Text.Json has been the Wolverine default since **5.0** (`UseSystemTextJsonForSerialization()` is wired in the `WolverineOptions` constructor). There is no 5.x Newtonsoft default to restore. If you want Newtonsoft as the default (the 4.x-and-earlier behavior), install [`WolverineFx.Newtonsoft`](#newtonsoft-json-moved-to-wolverinefx-newtonsoft-package-breaking) and call `opts.UseNewtonsoftForSerialization()` explicitly.
- **It does not change build-time things.** Dropping `net8.0` and moving to the JasperFx 2.0-alpha NuGet line are not runtime flags. Either move to net9+ or pin Wolverine 5.x.
- **It does not change package layout.** Newtonsoft moving to its own NuGet package is resolved by `dotnet add package WolverineFx.Newtonsoft`, not by a runtime call.

**Treat it as a temporary migration step.** The point of upgrading to 6.0 is to adopt the new defaults eventually; `RestoreV5Defaults()` exists so you can sequence the migration — adopt 6.0's API + bug fixes today, then remove the call and opt into each new default at your own pace.

### Newtonsoft.Json moved to `WolverineFx.Newtonsoft` package (BREAKING)

The core `WolverineFx` NuGet package no longer depends on Newtonsoft.Json. All Newtonsoft integration moved to a separate `WolverineFx.Newtonsoft` package and is exposed as **extension methods** rather than instance methods on `WolverineOptions` / `IEndpointConfiguration` / `IMassTransitInterop`.

If you call any of the following 5.x APIs:

- `WolverineOptions.UseNewtonsoftForSerialization(...)`
- `IEndpointConfiguration<T>.CustomNewtonsoftJsonSerialization(...)`
- `IMassTransitInterop.UseNewtonsoftForSerialization(...)`
- `new NewtonsoftSerializer(...)` directly

you must:

1. **Install the new package**: `dotnet add package WolverineFx.Newtonsoft`
2. **Add `using Wolverine.Newtonsoft;`** to bring the extension methods (and the `NewtonsoftSerializer` type) into scope.

The call surface itself is unchanged — `opts.UseNewtonsoftForSerialization(settings => …)` etc. work exactly as before, they're just extension methods now. See the [Newtonsoft.Json Serialization](/guide/messages.html#newtonsoft-json-serialization) section of the serialization docs.

Transports that pin a `NewtonsoftSerializer` internally for NServiceBus / MassTransit wire-compat (RabbitMQ's `UseNServiceBusInterop()`, the AWS SQS and SNS NServiceBus mappers, Azure Service Bus listeners) carry the `WolverineFx.Newtonsoft` dependency for you. You don't need to install it explicitly unless you call one of the Newtonsoft APIs from your own code.

In a related cleanup, `Subscription.Scope` no longer carries a `[Newtonsoft.Json.Converters.StringEnumConverter]` attribute — it's now annotated with `[System.Text.Json.Serialization.JsonStringEnumConverter]`. Wire format is unchanged (still string-named scopes). If you serialize `Subscription` instances yourself with Newtonsoft and rely on the string-named scope shape, configure `StringEnumConverter` on your own settings explicitly.

### `Wolverine.Http` Newtonsoft moved to `WolverineFx.Http.Newtonsoft` package (BREAKING)

Mirrors the core extraction above: the `WolverineFx.Http` NuGet package no longer depends on Newtonsoft.Json. All Newtonsoft-flavored HTTP serialization moved to a separate `WolverineFx.Http.Newtonsoft` package and is exposed as an **extension method** rather than an instance method on `WolverineHttpOptions`.

If you call the following 5.x API:

- `WolverineHttpOptions.UseNewtonsoftJsonForSerialization(...)`
- `new NewtonsoftHttpSerialization(...)` directly (rare — it was an internal-feeling singleton)

you must:

1. **Install the new package**: `dotnet add package WolverineFx.Http.Newtonsoft`
2. **Register the package's services** alongside `AddWolverineHttp()`:
   ```csharp
   builder.Services.AddWolverineHttp();
   builder.Services.AddWolverineHttpNewtonsoft();   // new
   ```
3. **Add `using Wolverine.Http.Newtonsoft;`** to bring the extension method (and the `NewtonsoftHttpSerialization` type) into scope.

The call surface itself is unchanged — `opts.UseNewtonsoftJsonForSerialization(settings => …)` works exactly as before, it's just an extension method now. See the [HTTP JSON serialization docs](/guide/http/json.html#using-newtonsoft-json) for the full example.

Core `WolverineFx.Http` retains the `JsonUsage.NewtonsoftJson` enum value so the public switch shape doesn't change. The codegen path inside `JsonResourceWriterPolicy` / `JsonBodyParameterStrategy` now dispatches through an internal `INewtonsoftHttpCodeGen` seam implemented in the new package; selecting `JsonUsage.NewtonsoftJson` without the package installed throws an `InvalidOperationException` at codegen time pointing you at `dotnet add package WolverineFx.Http.Newtonsoft`.

In a related cleanup, `ProblemDetailsContinuationPolicy.WriteProblems` (which dumps the `ProblemDetails` payload into the log line for the "found problems with this message" diagnostic) switched from `Newtonsoft.Json.JsonConvert.SerializeObject` to `System.Text.Json.JsonSerializer.Serialize`. This is a logging dump only — `ProblemDetails` round-trips cleanly through STJ; there is no observable output-format change.

### `IForwardsTo<T>` discovery is now explicit (BREAKING)

Wolverine 5.x scanned the application assembly at handler-graph compile time for every concrete type that implemented `IForwardsTo<T>` and wired the source-type → target-type forwarding automatically. That scan walks `Assembly.ExportedTypes` and is **not trim/AOT-clean** — so 6.0 removes it from the default startup path as part of the [AOT pillar](https://github.com/JasperFx/wolverine/issues/2746). Any application that relied on the implicit scan needs to opt in to one of two paths:

**Preferred upgrade path** — register each `IForwardsTo<T>` pair explicitly in your `UseWolverine` lambda:

```csharp
opts.RegisterMessageForwarder<PersonBorn, PersonBornV2>();
```

There's also a single-type-argument overload that derives the target type from the `IForwardsTo<>` closure (handy for attribute-driven helpers):

```csharp
opts.RegisterMessageForwarder<PersonBorn>(); // resolves to <PersonBorn, PersonBornV2>
```

Both forms feed `WolverineOptions.ExplicitMessageForwarders`, which is drained inside `HandlerGraph.Compile` — exactly where `Forwarders.FindForwards(Assembly)` used to run. The on-the-wire dispatch behavior (the older message type's `Transform()` builds the newer message, the newer message's handler runs) is unchanged.

**Escape hatch — opt back into the legacy scan:**

```csharp
opts.UseAutomaticForwarderDiscovery();
```

This re-enables the 5.x assembly scan against `WolverineOptions.ApplicationAssembly`. It is marked `[Obsolete]` and `[RequiresUnreferencedCode]` because the underlying scan walks exported types and the trimmer cannot prove which forwarder types are reachable. It is provided as a backward-compatibility escape valve for the 6.0 release cycle and is **slated for removal in 7.0** — migrate to `RegisterMessageForwarder<TFrom, TTo>()` before then.

If you don't use `IForwardsTo<T>` at all, no change is needed.

### `EventForwardingToWolverine()` removed (BREAKING)

Both `MartenConfigurationExpression.EventForwardingToWolverine(...)` extension method overloads (parameterless and the one taking `Action<IEventForwarding>`) were marked `[Obsolete]` in the 3.x line with a "will be removed in Wolverine 4.0" notice. They are now actually removed in 6.0.

The replacement has been available since 3.0: set `MartenIntegration.UseFastEventForwarding = true` inside the `IntegrateWithWolverine` configure callback. Event-subscription transformations (`SubscribeToEvent<T>().TransformedTo(...)`) live on the same `MartenIntegration` instance, so they move into the same callback.

**Before (5.x):**

```csharp
services.AddMarten(opts => opts.Connection(connString))
    .IntegrateWithWolverine()
    .EventForwardingToWolverine();
```

**After (6.0):**

```csharp
services.AddMarten(opts => opts.Connection(connString))
    .IntegrateWithWolverine(x => x.UseFastEventForwarding = true);
```

The `Action<IEventForwarding>` overload migrates to the same callback:

```csharp
services.AddMarten(opts => opts.Connection(connString))
    .IntegrateWithWolverine(x =>
    {
        x.UseFastEventForwarding = true;
        x.SubscribeToEvent<SecondEvent>()
            .TransformedTo(e => new SecondMessage(e.StreamId, e.Sequence));
    });
```

### `PulsarEndpoint.UriFor(...)` removed (BREAKING)

Both `PulsarEndpoint.UriFor(...)` overloads were `[Obsolete]` in the 5.x line and are now removed in 6.0:

- **`UriFor(string topicPath)`** — was a thin forwarder to `PulsarEndpointUri.Topic(topicPath)`. Replace any caller with `PulsarEndpointUri.Topic(...)` directly. Same signature, same return shape.

- **`UriFor(bool persistent, string tenant, string @namespace, string topicName)`** — returned a Pulsar-*native* topic path of the form `persistent://{tenant}/{ns}/{topic}` for hand-off to the native Pulsar client. That is NOT a Wolverine endpoint URI (`pulsar://...`); the two forms are deliberately not interchangeable. The internal Pulsar transport now uses a renamed helper `PulsarEndpoint.NativeTopicPath(...)` (also `internal`, same signature). External callers of the old `UriFor(bool, ...)` overload were rare-to-nonexistent — if you need this form for your own native-Pulsar interop, construct it directly:

  ```csharp
  var scheme = persistent ? "persistent" : "non-persistent";
  var nativePath = new Uri($"{scheme}://{tenant}/{@namespace}/{topicName}");
  ```

If you only ever called `UriFor(string)`, this is a one-line `using`-already-imported rename.

### Performance: per-endpoint serializer cache pre-population

The `Endpoint._serializers` cache that used to fill on first-miss at runtime is now pre-populated during `Endpoint.Compile()` with every globally-registered serializer. Effect for users: zero behavioral change; the hot-path serializer lookup is now a pure read.

Side note for users who register an `IMessageSerializer` *after* host startup: the post-Compile registration is still picked up via fallback to the global serializer registry on miss, but it's no longer cached on the endpoint. Either register the serializer before `StartAsync()` (the documented path) or use the endpoint-level `RegisterSerializer(...)` API on the specific endpoint that should see it.

### Performance conventions for contributors

Wolverine's hot-path dictionary lookups stay on `ImHashMap<TKey, TValue>` — this is now codified in [the contributors' CLAUDE.md](https://github.com/JasperFx/wolverine/blob/main/CLAUDE.md#performance-conventions). If you're contributing a perf PR and feel the urge to swap `ImHashMap` for `FrozenDictionary`, read that section first. The right fix for hot-path mutation is bootstrap-time pre-population in the relevant `Compile()` path, not a data-structure swap.

### Stale `DefaultSerializer` XmlDoc fixed

A long-standing XmlDoc on `WolverineOptions.DefaultSerializer` claimed Newtonsoft.Json as the default. System.Text.Json has actually been the default since Wolverine **5.0** (`UseSystemTextJsonForSerialization()` is wired in the default constructor). The XmlDoc was finally corrected in 6.0; no behavior change.

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

Better control over how [Wolverine generates code with respect to IoC container usage](/guide/codegen.html#wolverine-code-generation-and-ioc).

`IServiceContainer` moved to the `JasperFx` namespace.

By and large, we've *tried* to replace any API nomenclature using "master" with "main."

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/DuplicateMessageSending/Program.cs#L50-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_integrate_with_wolverine_with_multiple_options' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L44-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_http_services' title='Start of snippet'>anchor</a></sup>
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

<!-- snippet: sample_using_alwayspublishresponse -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Bugs/Bug_305_invoke_async_with_return_not_publishing_with_tuple_return_value.cs#L65-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_alwayspublishresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
