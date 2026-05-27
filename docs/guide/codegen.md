# Working with Code Generation

::: danger Wolverine 6.0 changes the IoC integration default
**`ServiceLocationPolicy.NotAllowed` is now the default.** If your DI registrations are opaque to Wolverine's
codegen (lambda factories with `Scoped` or `Transient` lifetime, mixed-scope `IEnumerable<T>`, etc.), the host
now throws `InvalidServiceLocationException` at startup instead of silently emitting a service-locator call.

**Two paths forward:**

1. **Preferred** — change the registration to a form Wolverine can see through: `AddScoped<TInterface, TImpl>()`
   instead of `AddScoped<TInterface>(sp => new TImpl(...))`, or constructor injection into the handler.
2. **Opt-in escape hatch** — list specific types Wolverine should service-locate, via
   `opts.CodeGeneration.AlwaysUseServiceLocationFor<TService>()`. This keeps the rest of the codegen
   constructor-inlined and only routes the listed types through the service locator.

If you want the 5.x behaviour back wholesale, set `opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn`
(or `AlwaysAllowed` to silence the warnings too). See the [IoC + Service Location section below](#wolverine-code-generation-and-ioc) for the full story
and the [5 → 6 migration guide](/guide/migration.html) for the rationale.
:::

::: warning
If you are experiencing noticeable startup lags or seeing spikes in memory utilization with an application using
Wolverine, you will want to pursue using either the `Auto` or `Static` modes for code generation as explained in this guide.
:::

Wolverine uses runtime code generation to create the "adaptor" code that Wolverine uses to call into 
your message handlers. Wolverine's [middleware strategy](/guide/handlers/middleware) also uses this strategy to "weave" calls to 
middleware directly into the runtime pipeline without requiring the copious usage of adapter interfaces
that is prevalent in most other .NET frameworks.

::: info
This page covers Wolverine-specific use of code generation. The shared JasperFx code-generation library that backs it — [frames](https://shared-libs.jasperfx.net/codegen/frames.html), [variables](https://shared-libs.jasperfx.net/codegen/variables.html), [`MethodCall`](https://shared-libs.jasperfx.net/codegen/method-call.html), [generated types](https://shared-libs.jasperfx.net/codegen/generated-types.html), and the [`codegen` CLI command](https://shared-libs.jasperfx.net/codegen/cli.html) — is documented at [shared-libs.jasperfx.net/codegen](https://shared-libs.jasperfx.net/codegen/). Reach for it when you're authoring a custom `IVariableSource` or middleware frame.
:::

That's great when everything is working as it should, but there's a couple issues:

1. The usage of the Roslyn compiler at runtime *can sometimes be slow* on its first usage. This can lead to sluggish *cold start*
   times in your application that might be problematic in serverless scenarios for examples.
2. There's a little bit of conventional magic in how Wolverine finds and applies middleware or passed arguments
   to your message handlers or HTTP endpoint handlers.

Not to worry though, Wolverine has several facilities to either preview the generated code for diagnostic purposes to 
really understand how Wolverine is interacting with your code and to optimize the "cold start" by generating the dynamic
code ahead of time so that it can be embedded directly into your application's main assembly and discovered from there.

By default, Wolverine runs with "dynamic" code generation where all the necessary generated types are built on demand
the first time they are needed. This is perfect for a quick start to Wolverine, and might be fine in smaller projects even
at production time.

::: warning
Note that you may need to delete the existing source code when you change
handler signatures or add or remove middleware. Nothing in Wolverine is able
to detect that the generated source code needs to be rewritten
:::

Lastly, you have a couple options about how Wolverine handles the dynamic code generation as shown below:

<!-- snippet: sample_codegen_type_load_mode -->
<a id='snippet-sample_codegen_type_load_mode'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default behavior. Dynamically generate the
        // types on the first usage
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

        // Never generate types at runtime, but instead try to locate
        // the generated types from the main application assembly
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

        // Hybrid approach that first tries to locate the types
        // from the application assembly, but falls back to
        // generating the code and dynamic type. Also writes the
        // generated source code file to disk
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L13-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_codegen_type_load_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At development time, use the `Dynamic` mode if you are actively changing handler
signatures or the application of middleware that might be changing the generated code. 

Even at development time, if the handler signatures are relatively stable, you can use
the `Auto` mode to use pre-generated types locally. This may help you have a quicker
development cycle -- especially if you like to lean heavily on integration testing where
you're quickly starting and stopping your application. The `Auto` mode will write the generated
source code for missing types to the `Internal/Generated` folder under your main application 
project.

::: tip
If you're using the `Auto` mode in combination with `dotnet watch` you need to disable the watching of
the `Internal/Generated` folder to avoid application restarts each time codegen writes a new file.
You can do this by adding the following to the `.csproj` file of your app project.

```xml
<ItemGroup>
    <Compile Update="Internal\Generated\**\*.cs" Watch="false" />
</ItemGroup>
```

:::


At production time, if there is any issue whatsoever with resource utilization, the Wolverine team
recommends using the `Static` mode where all types are assumed to be pre-generated into what Wolverine
thinks is the application assembly (more on this in the troubleshooting guide below).

::: tip
Most of the facilities shown here will require the [Oakton command line integration](./command-line).
:::

## The `WolverineFx.RuntimeCompilation` Package <Badge type="tip" text="5.34" />

Runtime code generation requires a Roslyn-backed `IAssemblyGenerator` to compile the generated C# source into an assembly that Wolverine can load. That implementation ships in `JasperFx.RuntimeCompiler`, which pulls in roughly 100 MB of Roslyn assemblies. For applications that pre-generate all of their handler / middleware code with `TypeLoadMode.Static` and never recompile at runtime, those assemblies are dead weight — bigger deployment, slower cold start, and incompatible with Native AOT.

The `WolverineFx.RuntimeCompilation` package isolates that runtime-compilation dependency in a separate package, so production deployments that don't need it ship without Roslyn.

::: warning Changed in 6.0
**As of Wolverine 6.0, core `WolverineFx` no longer references `JasperFx.RuntimeCompiler` or registers a default `IAssemblyGenerator`.** Applications running `TypeLoadMode.Dynamic` (the default) or `Auto` now **must** reference `WolverineFx.RuntimeCompilation` — simply referencing the package is enough (it auto-registers the Roslyn `IAssemblyGenerator` via a `[WolverineModule]`). `TypeLoadMode.Static` apps that pre-generate all of their code need nothing and ship without Roslyn. See [Migrating from 5 to 6](/guide/migration). On 5.x this package was an optional, forward-looking opt-in; in 6.0 it is required for runtime code generation. Part of the cold-start / AOT-readiness work in [#1577](https://github.com/JasperFx/wolverine/issues/1577) / [#2876](https://github.com/JasperFx/wolverine/issues/2876).
:::

### When you need it

| Scenario | Need the package? |
|----------|-------------------|
| Local development with `TypeLoadMode.Dynamic` (the default) | **Yes** — runtime compilation runs every time. |
| `TypeLoadMode.Auto` with the source-code fall-back | **Yes** — compilation runs the first time a missing handler is invoked. |
| Production with `TypeLoadMode.Static` and all generated code pre-built into the application assembly | **No** — runtime compilation is never invoked. In 6.0 core `WolverineFx` no longer carries Roslyn, so a Static-mode deployment ships without it automatically. |
| Running `dotnet run -- codegen write` from a CI build agent | **Yes** — that command emits the generated source by compiling it once. |

### Installation

```sh
dotnet add package WolverineFx.RuntimeCompilation
```

The package depends on `JasperFx.RuntimeCompiler`. It does not pull in any other Wolverine concerns.

### Configuration

In 6.0, **referencing the package is usually all you need**: `WolverineFx.RuntimeCompilation` ships a `[WolverineModule]` that Wolverine auto-discovers at startup and which registers the Roslyn `IAssemblyGenerator` for you. With the default extension auto-discovery on, `TypeLoadMode.Dynamic`/`Auto` "just works" once the package is referenced — no code change required.

You can also register it explicitly inside `UseWolverine(...)` via the `UseRuntimeCompilation()` extension method. This is **required** if you've turned extension auto-discovery off (`ExtensionDiscovery.ManualOnly`), and is handy when you want to gate it by environment (see below):

```csharp
using Wolverine;

builder.Host.UseWolverine(opts =>
{
    opts.UseRuntimeCompilation();

    // ...the rest of your Wolverine configuration
});
```

`UseRuntimeCompilation()` is idempotent — calling it twice (or alongside the auto-registering module) does nothing on the second call. It registers `IAssemblyGenerator` as a singleton in DI using `TryAddSingleton`, so a custom registration you've already added wins.

For advanced scenarios where you need to register the runtime compiler outside of `UseWolverine(...)` (e.g., from a hosted-service registration ordering), the `IServiceCollection` overload is available:

```csharp
using Wolverine;

builder.Services.AddWolverineRuntimeCompilation();
```

### Recommended pattern: dev-time-only

The cleanest deployment shape is to take the package as a `<PackageReference>` for the whole project but only register it in development:

```csharp
builder.Host.UseWolverine(opts =>
{
    if (builder.Environment.IsDevelopment())
    {
        opts.UseRuntimeCompilation();
    }

    // ...the rest of your Wolverine configuration
});
```

Combine that with `TypeLoadMode.Static` in production and the `dotnet run -- codegen write` step in your build pipeline (see [Embedding Codegen in Docker](#embedding-codegen-in-docker) below), and your production deployment never invokes Roslyn.

### Dropping Roslyn from the production image entirely

The dev-time-only pattern above keeps `WolverineFx.RuntimeCompilation` referenced for the *whole* project, so the ~100 MB of Roslyn assemblies still ship in the production image — they're simply never registered. To actually **remove Roslyn from production builds**, make the package reference conditional on the build configuration and let production run pre-generated code in `Static` mode:

```xml
<!-- Referenced only in Debug, so a Release publish ships without Roslyn. -->
<ItemGroup Condition="'$(Configuration)' == 'Debug'">
  <PackageReference Include="WolverineFx.RuntimeCompilation" Version="6.0.0" />
</ItemGroup>
```

```csharp
using JasperFx;
using JasperFx.CodeGeneration;

// Production runs the pre-generated code with no runtime compilation.
// AssertAllPreGeneratedTypesExist (default: false) makes a missing or stale
// generated type fail fast at startup instead of silently misbehaving.
builder.Services.CritterStackDefaults(x =>
{
    x.Production.GeneratedCodeMode = TypeLoadMode.Static;
    x.Production.AssertAllPreGeneratedTypesExist = true;
});
```

Because `WolverineFx.RuntimeCompilation` **auto-registers** when referenced (via its `[WolverineModule]`), this pattern needs **no `UseRuntimeCompilation()` call** — and that's exactly what lets you drop the package in Release without a compile error. In `Debug` the package is present and `Dynamic` mode compiles at runtime; in a `Release`/`Production` build the package is gone and `Static` mode runs the pre-generated code.

::: warning
The condition must use `$(Configuration)` **with the parentheses**. A malformed condition such as `'$Configuration)' != 'Release'` silently never matches, so the assembly keeps shipping anyway — a common cause of "I excluded the package but Roslyn is still in my image."
:::

A complete, tested end-to-end example — `Program.cs`, the conditional `.csproj`, committed `Internal/Generated/`, a multi-stage `Dockerfile`, a `verify-production-build.sh` script that asserts the Release publish is Roslyn-free, and an Alba test that boots the app in the `Production` environment — lives in the **[CqrsMinimalApi sample](https://github.com/JasperFx/CritterStackSamples/tree/main/CqrsMinimalApi)**.

### What happens if you forget?

In 6.0, core `WolverineFx` no longer registers a default `IAssemblyGenerator`. A `TypeLoadMode.Dynamic`/`Auto` app that starts without the runtime compiler available fails fast at startup with:

> Wolverine is running in TypeLoadMode.Dynamic, which compiles handler/middleware code at runtime, but no IAssemblyGenerator (Roslyn) is registered. Core WolverineFx no longer ships the runtime compiler. Either add the 'WolverineFx.RuntimeCompilation' NuGet package (it auto-registers when referenced, or call opts.UseRuntimeCompilation() in UseWolverine(...)), or pre-generate code with 'dotnet run -- codegen write' and set opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static.

To resolve it: reference `WolverineFx.RuntimeCompilation` (Dynamic/Auto mode), or move to `TypeLoadMode.Static` with pre-generated code (production, Roslyn-free).

(On 5.x, `WolverineFx` registered a default `IAssemblyGenerator`, so the package was an optional forward-looking opt-in. 6.0 removed that default — hence the requirement above.)

## Embedding Codegen in Docker

The sweet spot for production deployments is `Dynamic` codegen at development time, then pre-generated code artifacts baked into the production image so cold start never pays the runtime-codegen cost. A multi-stage Dockerfile that runs `dotnet run -- codegen write` in the build stage gets you there.

### Wire up the CLI command

`dotnet run -- codegen write` is provided by the JasperFx command-line integration that ships with Wolverine. The last line of your `Program.cs` needs to hand control to it:

```csharp
return await app.RunJasperFxCommands(args);
```

Without this, the `codegen write` verb is unreachable and the build-stage step below will fail.

### A multi-stage Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY ["Application/Application.csproj", "Application/"]

# Add more COPY lines for any project references your app needs
# COPY ["Shared/Shared.csproj", "Shared/"]

COPY . .
WORKDIR "/src/Application"

# Pre-generate Wolverine handler / endpoint adapter code into
# Application/Internal/Generated/ so the production image ships static C#
# instead of compiling at boot. Pair with TypeLoadMode.Static in production.
RUN dotnet run -- codegen write
RUN dotnet publish "Application.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
ENV DOTNET_RUNNING_IN_CONTAINER=1
ENV DOTNET_NOLOGO=1
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
RUN addgroup -g 1001 -S nonroot && adduser -u 1001 -S nonroot -G nonroot
RUN mkdir /app
RUN chown nonroot:nonroot /app
WORKDIR /app
COPY --chown=nonroot:nonroot --from=build /app/publish .

FROM runtime
EXPOSE 5000
USER nonroot
ENTRYPOINT ["dotnet", "Application.dll"]
```

The base image tags above (`9.0-alpine`) match Wolverine 6.0's minimum TFM (`net9.0`). Pick whichever LTS your app targets — `10.0-alpine` works the same way once you're on `net10.0`.

### The `codegen write` step has no runtime resources

This is the constraint that bites people: `dotnet run -- codegen write` boots the host far enough to discover handlers and HTTP endpoints, then exits before serving traffic. If anything in your `Program.cs` reaches out to a database, broker, or other external resource _before_ control reaches `RunJasperFxCommands(args)`, the build-stage step will block or fail because none of that infrastructure is reachable from inside the Dockerfile's build container.

Two ways to dodge it:

1. **Defer infrastructure to hosted services / DI factories.** Things like `PersistMessagesWithPostgresql(...)`, transport listener registrations, and resource-setup-on-startup all defer their actual I/O to host startup — they're already safe. The trap is custom code in `Program.cs` that eagerly connects (e.g. an inline `await dataSource.OpenConnectionAsync()` before `app.Run()` / `RunJasperFxCommands`).
2. **Detect the codegen verb and short-circuit external wiring.** Same pattern that works for Aspire — see [the Aspire / OpenAPI codegen section](#handling-code-generation-with-wolverine-when-using-aspire-or-microsoft-extensions-apidescription-server) below — wrap conditionally-disabled transports / persistence inside `if (CodeGeneration.IsRunningGeneration())` so the build-stage run skips them.

### Beyond Static: Native AOT

Pre-generated codegen is the prerequisite for publishing the app with Native AOT (`dotnet publish /p:PublishAot=true`). Once the Dockerfile above is producing a `Static`-mode image, the [AOT publishing guide](/guide/aot.md) covers the additional `IsAotCompatible` / trimmer-annotation pieces needed to shrink the production image further and skip the runtime-compilation pipeline entirely.

## Troubleshooting Code Generation Issues

::: warning
There's nothing magic about the `Auto` mode, and Wolverine isn't (yet) doing any file comparisons against the generated code and
the current version of the application. At this point, the Wolverine community recommends against using the `Auto` mode
for code generation as it has not added much value and can cause some confusion.
:::

In all cases, don't hesitate to reach out to the Wolverine team in the Discord link at the top right of this page to 
ask for help with any codegen related issues.

If Wolverine is throwing exceptions in `Static` mode saying that it cannot find the expected pre-generated types, here's 
your checklist of things to check:

Are the expected generated types written to files in the main application project before that project is compiled? The pre-generation
works by having the source code written into the assembly in the first place.

Is Wolverine really using the correct application assembly when it looks for pre-built handlers or HTTP endpoints? Wolverine will log
what *it* thinks is the application assembly upfront, but it can be fooled in certain project structures. To override the application
assembly choice to help Wolverine out, use this syntax:

<!-- snippet: sample_overriding_application_assembly -->
<a id='snippet-sample_overriding_application_assembly'></a>
```cs
using var host = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Override the application assembly to help
        // Wolverine find its handlers
        // Should not be necessary in most cases
        opts.ApplicationAssembly = typeof(Program).Assembly;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/BootstrappingSamples.cs#L10-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_application_assembly' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If the assembly choice is correct, and the expected code files are really in `Internal/Generated` exactly as you'd expect, make
sure there's no accidental `<Exclude />` nodes in your project file. *Don't laugh, that's actually happened to Wolverine users*

::: warning
Actually, while the Wolverine team mostly uses JetBrains Rider that doesn't exhibit this behavior, we found out the hard way interacting with other folks that
Visual Studio.Net will add the `<Exclude />` into your `csproj` file when you manually delete the generated code files
sometimes.
:::

If you see issues with *Marten* document providers, make sure that you have registered that document with Marten itself. At this point,
Wolverine does not automatically register `Saga` types with Marten. See [Marten's own documentation](https://martendb.io) about document type discovery.

## Wolverine Code Generation and IoC <Badge type="tip" text="5.0" />

::: info
Why, you ask, does Wolverine do any of this? Wolverine was originally conceived of as the successor to the 
[FubuMVC & FubuTransportation](https://fubumvc.github.io) projects from the early 2010's. A major lesson learned
from FubuMVC was that we needed to reduce object allocations, layering, runaway `Exception` stack traces, and allow
for more flexible and streamlined handler or endpoint method signatures. To that end we fully embraced using runtime code
generation -- and this was built well before source generators were available. 

As for the IoC part of this strategy, we ask you, what's the very fastest IoC tool in .NET? The answer of course, is 
"no IoC container."
:::

Wolverine's code generation uses the configuration of your IoC tool to create the generated code wrappers 
around your raw message handlers, HTTP endpoints, and middleware methods. Whenever possible, Wolverine is trying to
completely eliminate your application's IoC tool from the runtime code by generating the necessary constructor function
invocations to exactly mimic your application's IoC configuration. 

::: info
Because you should care about this, Wolverine is absolutely generating `using` or `await using` for any objects it
creates through constructor calls that implements `IDisposable` or `IAsyncDisposable`.
:::

When generating the adapter classes, Wolverine can infer which method arguments or type dependencies can be sourced
from your application's IoC container configuration. If Wolverine can determine a way to generate all the necessary
constructor calls to create any necessary services registered with a `Scoped` or `Transient` lifetime, Wolverine will generate
code with the constructors. In this case, any IoC services that are registered with a `Singleton` lifetime
will be "inlined" as constructor arguments into the generated adapter class itself for a little better efficiency.

::: warning
The usage of a service locator within the generated code will naturally be a little less efficient just because there
is more runtime overhead. More dangerously, the service locator usage can sometimes foul up the scoping of services
like Wolverine's `IMessageBus` or Marten's `IDocumentSession` that are normally built outside of the IoC container
:::

If Wolverine cannot determine a path to generate
code for raw constructor construction of any registered services for a message handler or HTTP endpoint, Wolverine 
will fall back to generating code with the [service locator pattern](https://en.wikipedia.org/wiki/Service_locator_pattern) 
using a scoped container (think [IServiceScopeFactory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.iservicescopefactory?view=net-9.0-pp)).

Here's some facts you do need to know about this whole process:

* The adapter classes generated by Wolverine for both message handlers and HTTP endpoints are effectively singleton
  scoped and only ever built once
* Wolverine will try to bring `Singleton` scoped services through the generated adapter type's constructor function *one time*
* Wolverine will have to fall back to the service locator usage if any service dependency that has a `Scoped` or `Transient`
  lifetime is either an `internal` type or uses an "opaque" Lambda registration (think `IServiceCollection.AddScoped(s => {})`)

::: tip
The code generation using IoC configuration is tested with the built in .NET `ServiceProvider`. It
is theoretically possible to use other IoC tools with Wolverine, but only if you are *only* using `IServiceCollection`
for your IoC configuration.
:::

As of Wolverine 5.0, you can control the usage of the service locator in Wolverine's code generation:

::: warning Default changed in 6.0
The default `ServiceLocationPolicy` is now `NotAllowed` (was `AllowedButWarn` in 5.x). Any code path that
previously emitted a "Utilizing service location for…" warning now throws `InvalidServiceLocationException`
at host startup. See the [LOUD callout at the top of this page](#working-with-code-generation) and the
[5 → 6 migration guide](/guide/migration.html) for the upgrade path.
:::

<!-- snippet: sample_configuring_servicelocationpolicy -->
<a id='snippet-sample_configuring_servicelocationpolicy'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // This is the default behavior. Wolverine will allow you to utilize
    // service location in the codegen, but will warn you through log messages
    // when this happens
    opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

    // Tell Wolverine to just be quiet about service location and let it
    // all go. For any of you with small children, I defy you to get the 
    // Frozen song out of your head now...
    opts.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

    // Wolverine will throw exceptions at runtime if it encounters
    // a message handler or HTTP endpoint that would require service
    // location in the code generation
    // Use this option to disallow any undesirably service location
    opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed;
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ServiceLocationUsage.cs#L11-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_servicelocationpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: note
[Wolverine.HTTP has some additional control over the service locator](/guide/http/#using-the-httpcontext-requestservices) to utilize the shared scoped container
with the rest of the AspNetCore pipeline. 
:::

## Allow List for Service Location <Badge type="tip" text="5.0" />

Wolverine always reverts to using a service locator when it encounters an "opaque" Lambda registration that has either
a `Scoped` or `Transient` service lifetime. You can explicitly create an "allow" list of service types that can use
a service locator pattern while allowing the rest of the code generation for the message handler or HTTP endpoint to use
the more predictable and efficient generated constructor functions with this syntax:

<!-- snippet: sample_always_use_service_location -->
<a id='snippet-sample_always_use_service_location'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // other configuration

    // Use a service locator for this service w/o forcing the entire
    // message handler adapter to use a service locator for everything
    opts.CodeGeneration.AlwaysUseServiceLocationFor<IServiceGatewayUsingRefit>();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/CodeGeneration/service_location_assertions.cs#L45-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_always_use_service_location' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For example, this functionality might be helpful for:

* [Refit proxies](https://github.com/reactiveui/refit) that are registered in IoC with a Lambda registration, but might not use any other services
* EF Core `DbContext` types that might require some runtime configuration to construct themselves, but don't use other services (a [JasperFx Software](https://jasperfx.net) client
  ran into this needing to conditionally opt into read replica usage, so hence, this feature made it into Wolverine 5.0)

## Environment Check for Expected Types

As a new option in Wolverine 1.7.0, you can also add an environment check for the existence of the expected pre-built types
to [fail fast](https://en.wikipedia.org/wiki/Fail-fast) on application startup like this:

<!-- snippet: sample_asserting_all_pre_built_types_exist_upfront -->
<a id='snippet-sample_asserting_all_pre_built_types_exist_upfront'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
    {
        if (builder.Environment.IsProduction())
        {
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

            opts.Services.CritterStackDefaults(cr =>
            {
                // I'm only going to care about this in production
                cr.Production.AssertAllPreGeneratedTypesExist = true;
            });
        }
    });

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L37-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asserting_all_pre_built_types_exist_upfront' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Do note that you would have to opt into using the environment checks on application startup, and maybe even force .NET
to make hosted service failures stop the application. 

See [Oakton's Environment Check functionality](https://jasperfx.github.io/oakton/guide/host/environment.html) for more information (the old Oakton documentation is still relevant for
JasperFx). 

## Previewing the Generated Code

::: tip
All of these commands are from the JasperFx.CodeGeneration.Commands library that Wolverine adds as 
a dependency. This is shared with [Marten](https://martendb.io) as well. See the [`codegen` CLI reference](https://shared-libs.jasperfx.net/codegen/cli.html) for every subcommand and flag.
:::

To preview the generated source code, use this command line usage from the root directory of your .NET project:

```bash
dotnet run -- codegen preview
```

## Generating Code Ahead of Time

To write the source code ahead of time into your project, use:

```bash
dotnet run -- codegen write
```

This command **should** write all the source code files for each message handler and/or HTTP endpoint handler to `/Internal/Generated/WolverineHandlers`
directly under the root of your project folder.

## Handling Code Generation with Wolverine when using Aspire or Microsoft.Extensions.ApiDescription.Server

When integrating **Wolverine** with **Aspire**, or using `Microsoft.Extensions.ApiDescription.Server` to generate OpenAPI files at build time, you may encounter issues with code generation because connection strings are only provided by Aspire when the application is run.
This limitation affects both Wolverine codegen and OpenAPI schema generation, because these processes require connection strings during their execution.

To work around this, add a helper class that detects if we are just generating code (either by the Wolverine codegen command or during OpenAPI generation).
You can then conditionally disable external Wolverine transports and message persistence to avoid configuration errors.

```csharp
public static class CodeGeneration
{
    public static bool IsRunningGeneration()
    {
        return Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider" || Environment.GetCommandLineArgs().Contains("codegen");
    }
}
```

Example use
```csharp
if (CodeGeneration.IsRunningGeneration())
{
    builder.Services.DisableAllExternalWolverineTransports();
    builder.Services.DisableAllWolverineMessagePersistence();
}

builder.Services.AddWolverine(options =>
{
   var connectionString = builder.Configuration.GetConnectionString("postgres");
   if (CodeGeneration.IsRunningGeneration() == false)
   {
       var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
       options.PersistMessagesWithPostgresql(dataSource, "wolverine");
   }
}
```

## Optimized Workflow

Wolverine and [Marten](https://martendb.io) both use the shared JasperFx library for their code generation, 
and you can configure different behavior for production versus development time for both tools (and any future
"CritterStack" tools) with this usage:

<!-- snippet: sample_use_optimized_workflow -->
<a id='snippet-sample_use_optimized_workflow'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Use "Auto" type load mode at development time, but
        // "Static" any other time
        opts.Services.CritterStackDefaults(x =>
        {
            x.Production.GeneratedCodeMode = TypeLoadMode.Static;
            x.Production.ResourceAutoCreate = AutoCreate.None;

            // Little draconian, but this might be helpful
            x.Production.AssertAllPreGeneratedTypesExist = true;

            // These are defaults, but showing for completeness
            x.Development.GeneratedCodeMode = TypeLoadMode.Dynamic;
            x.Development.ResourceAutoCreate = AutoCreate.CreateOrUpdate;
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L61-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_optimized_workflow' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Which will use:

1. `TypeLoadMode.Dynamic` when the .NET environment is "Development" and dynamically generate types on the first usage
2. `TypeLoadMode.Static` for other .NET environments for optimized cold start times

## Customizing the Generated Code Output Path

By default, Wolverine writes generated code to `Internal/Generated` under your project's content root.
For Console applications or non-standard project structures, you may need to customize this path.

### Using CritterStackDefaults

`CritterStackDefaults` is the shared entry point for opinionated defaults across the Critter Stack (Wolverine, Marten, Polecat, …). Full reference: [shared-libs.jasperfx.net/configuration/critter-stack-defaults](https://shared-libs.jasperfx.net/configuration/critter-stack-defaults.html).

You can configure the output path globally for all Critter Stack tools:

<!-- snippet: sample_configure_generated_code_output_path -->
<a id='snippet-sample_configure_generated_code_output_path'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.Services.CritterStackDefaults(opts =>
{
    // Set a custom output path for generated code
    opts.GeneratedCodeOutputPath = "/path/to/your/project/Internal/Generated";
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L86-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configure_generated_code_output_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Auto-Resolving Project Root for Console Apps

Console applications often have `ContentRootPath` pointing to the `bin` folder, which causes
generated code to be written to the wrong location. Enable automatic project root resolution:

<!-- snippet: sample_auto_resolve_project_root -->
<a id='snippet-sample_auto_resolve_project_root'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.Services.CritterStackDefaults(opts =>
{
    // Automatically find the project root by looking for .csproj/.sln files
    // Useful for Console apps where ContentRootPath defaults to bin folder
    opts.AutoResolveProjectRoot = true;
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L99-L108' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_auto_resolve_project_root' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Direct Wolverine Configuration

You can also configure the path directly on Wolverine:

<!-- snippet: sample_direct_wolverine_output_path -->
<a id='snippet-sample_direct_wolverine_output_path'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts.CodeGeneration.GeneratedCodeOutputPath = "/path/to/output";
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CodegenUsage.cs#L113-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_direct_wolverine_output_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that explicit Wolverine configuration takes precedence over `CritterStackDefaults`.

## Custom Variable Sources — Teaching Codegen to Resolve Your Types <Badge type="tip" text="5.32" />

Wolverine's codegen resolves handler parameters out of the service container, message body, HTTP route, and other built-in sources. For types it doesn't know how to build — strong-typed identifiers, correlation tokens, sequence-generated values — you can register an `IVariableSource` from JasperFx's codegen subsystem and tell Wolverine exactly how to materialize the value at runtime.

A common motivating case: **generating a strong-typed identifier from a database sequence before an aggregate is created.** In a plain handler you'd have to inject the session and call an async helper yourself:

```csharp
// The pattern we want to move away from
public static async Task<(ReportStarted, IMartenOp)> Handle(
    StartReport command,
    IDocumentSession session,
    CancellationToken ct)
{
    var id = await session.GetNextReportId(ct);                 // async ID fetch in the handler body
    var report = new Report(id) { Name = command.Name };
    return (new ReportStarted(command.Name, id), MartenOps.Store(report));
}
```

This forces the handler to be async solely for the id lookup, makes `IDocumentSession` a hard dependency, and pulls infrastructure concerns into the message handler.

An `IVariableSource` lets you pull the id directly into the handler's parameter list. The handler stays focused on the domain, while Wolverine's codegen weaves in the factory call behind the scenes:

```csharp
public static (ReportStarted, IMartenOp) Handle(
    StartReport command,
    ReportId id)                                                // Wolverine resolves this via ReportIdSource
{
    var report = new Report(id) { Name = command.Name };
    return (new ReportStarted(command.Name, id), MartenOps.Store(report));
}
```

### 1. Define the strong-typed id and its factory

```csharp
// The strong-typed id — use Vogen / StronglyTypedId in real code
// to get equality, serialization, and validation for free.
public record ReportId(int Number);

public static class DocumentSessionExtensions
{
    public static async Task<ReportId> GetNextReportId(
        this IDocumentSession session,
        CancellationToken cancellation)
    {
        var number = await session.NextSequenceValue("reports.report_sequence", cancellation);
        return new ReportId(number);
    }
}
```

The sequence itself is registered via Marten's extended schema objects:

```csharp
builder.Services.AddMarten(opts =>
{
    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "reports";

    // Marten will create/maintain this sequence alongside your document schema.
    opts.Storage.ExtendedSchemaObjects.Add(new Sequence("report_sequence"));
}).IntegrateWithWolverine();
```

### 2. Implement `IVariableSource`

`IVariableSource` lives in `JasperFx.CodeGeneration.Model`. It advertises which types it can materialize (`Matches`) and emits the code fragment that produces them (`Create`):

```csharp
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

internal class ReportIdSource : IVariableSource
{
    public bool Matches(Type type) => type == typeof(ReportId);

    public Variable Create(Type type)
    {
        // MethodCall models a call to DocumentSessionExtensions.GetNextReportId(session, cancellation).
        // Arguments (session, ct) are resolved automatically — they're already in scope as other
        // variables in the generated handler.
        var call = new MethodCall(
            typeof(DocumentSessionExtensions),
            nameof(DocumentSessionExtensions.GetNextReportId))
        {
            CommentText = "Creating a new ReportId"
        };

        // The method's return variable is the one we're being asked for.
        return call.ReturnVariable!;
    }
}
```

Two things to notice:

- You only describe how to create the value. Wolverine handles the `await`, the lifetime of the dependency (`IDocumentSession`), and where the fragment lands inside the generated handler.
- Because the `MethodCall` is async, every handler that takes a `ReportId` parameter becomes async under the hood — even if your source code declares the handler as synchronous. Wolverine's codegen rewrites the method signature for you.

### 3. Register the source

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.CodeGeneration.Sources.Add(new ReportIdSource());
});
```

From here on, any handler (or Wolverine HTTP endpoint) that declares a `ReportId` parameter gets one generated for it automatically.

### Why not `LoadAsync`?

Wolverine's [A-Frame `LoadAsync` pattern](/guide/handlers/middleware) is the go-to when you need to *load an existing aggregate* before the handler runs. Custom id generation has the same ergonomic goal — pull infrastructure calls out of `Handle` — but the result is a *new* value rather than a retrieved aggregate, so `IVariableSource` is a better fit. You can freely mix the two styles inside one handler: a `ReportId` materialized from an `IVariableSource` alongside a parent aggregate loaded via a `LoadAsync` method.

### Previewing the generated code

Run `dotnet run -- codegen preview` and look at the generated handler class. The fragment injected by `ReportIdSource` is clearly labelled with the `CommentText` you supplied:

```csharp
// Creating a new ReportId
var reportId = await DocumentSessionExtensions.GetNextReportId(session, cancellation);

var report = new Report(reportId) { Name = command.Name };
// ...
```

If the preview shows the variable being service-located or falling back to a default constructor, check that `Matches` is returning `true` for your exact type and that you registered the source before the first handler is generated.

### Full sample

A complete runnable project covering the above is at [CritterStackSamples/Reports](https://github.com/JasperFx/CritterStackSamples/tree/main/Reports).
