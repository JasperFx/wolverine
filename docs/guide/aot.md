# AOT Publishing

::: warning
**Status: in progress.** Wolverine 6.0 sets up the AOT story but the full per-file annotation pass is still landing — see [the AOT-pillar tracking issue](https://github.com/JasperFx/wolverine/issues/2746). The guidance below describes the **target** end state plus the safe escape hatches that work today.
:::

Wolverine 6.0 introduces first-class support for publishing applications with [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) (`dotnet publish /p:PublishAot=true`) and aggressive trimming. The story is two-part:

1. **At build time** — pre-generate handler dispatch code via `dotnet run -- codegen write`, then ship that pre-generated code as ordinary, statically-analyzable C# inside your app's `Internal/Generated/` folder.
2. **At runtime** — configure Wolverine in `TypeLoadMode.Static` so the host loads the pre-generated types instead of compiling them at boot. With Static mode in effect, Wolverine's Roslyn-based `AssemblyGenerator` is never invoked, and the AOT compiler / trimmer have everything they need statically.

## Why this matters

Wolverine's default `TypeLoadMode.Dynamic` (and `Auto`) configurations compile handler dispatch via [JasperFx.RuntimeCompiler](https://github.com/JasperFx/jasperfx/tree/main/src/JasperFx.RuntimeCompiler) on host startup. That path:

- Loads `Microsoft.CodeAnalysis.CSharp` (~6 MB Roslyn binaries) into your process
- Emits and JIT-compiles new types at runtime — fundamentally incompatible with Native AOT
- Reflects over handler / saga / middleware types in ways the trimmer can't follow statically

`TypeLoadMode.Static` shifts all of that to build time. Production binaries published with `Static` mode + pre-generated code do **not** carry Roslyn at all, start faster, and pass `dotnet publish /p:PublishAot=true` without `IL2026` / `IL3050` warnings outside the explicitly-annotated runtime-codegen surface.

## Walkthrough

### 1. Pre-generate handler code

In your project root, run:

```bash
dotnet run -- codegen write
```

This invokes the JasperFx command-line surface that Wolverine extends. The command discovers handlers / sagas / middleware in the configured assemblies, runs the same code-generation passes the host would run at startup, and writes the resulting C# into `Internal/Generated/` (or wherever `opts.CodeGeneration.GeneratedCodeOutputPath` points). The files are normal C# — check them in, run them through the trimmer, set breakpoints in them.

::: tip
`codegen write` is the same command that's been available in earlier Wolverine versions for dev-time inspection. The 6.0 difference is that the **output is byte-stable and round-trips into `TypeLoadMode.Static`** — i.e. you can rely on the pre-generated files being the authoritative dispatch code, not just a debugging aid.
:::

### 2. Configure `TypeLoadMode.Static`

In your application's `Program.cs`:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
    // … the rest of your configuration
});
```

`TypeLoadMode.Static` tells Wolverine "the pre-generated handler types are in this assembly; load them by name and skip the runtime compile step entirely." If a handler is missing pre-generated code, host startup fails with a clear error pointing at the missing file — so the failure mode is "loud and at startup" rather than "silent fallback to Roslyn."

### 3. Publish AOT

Reference `PublishAot` in your csproj or pass it on the command line:

```bash
dotnet publish -c Release /p:PublishAot=true
```

A clean AOT-published Wolverine app in `Static` mode emits **no** `IL2026` / `IL3050` warnings from Wolverine itself today against the AOT-clean *subset* of the surface (`Envelope`, `WolverineOptions`, `DeliveryOptions`, the scheduling helpers — what the [`Wolverine.AotSmoke`](https://github.com/JasperFx/wolverine/tree/main/src/Testing/Wolverine.AotSmoke) regression-guard project exercises).

::: warning
Until the [AOT-pillar tracking issue (#2746)](https://github.com/JasperFx/wolverine/issues/2746) is fully closed out, Wolverine APIs outside the AOT-clean subset above may emit IL warnings under `PublishAot=true`. The warnings are informative — they tell you which Wolverine APIs require dynamic code / aren't trim-safe. The Static-mode runtime path avoids most of them; the rest get annotated as the pillar work progresses.
:::

## What `Wolverine.AotSmoke` already guarantees

The [`Wolverine.AotSmoke`](https://github.com/JasperFx/wolverine/tree/main/src/Testing/Wolverine.AotSmoke) project in the Wolverine repo sets `IsAotCompatible=true`, `TrimMode=full`, and promotes the `IL2026/IL2046/IL2055/IL2065/IL2067/IL2070/IL2072/IL2075/IL2090/IL2091/IL2111/IL3050/IL3051` warning codes to errors. Every PR runs it via the [`aot` workflow](https://github.com/JasperFx/wolverine/blob/main/.github/workflows/aot.yml).

Currently exercised AOT-clean surface (will fail the smoke build if any of these APIs gains a `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` annotation):

- `Envelope` construction + value-shape (`Id`, `Headers`, `CorrelationId`, `MessageType`, …)
- `Envelope.TryGetHeader` fast path
- `Envelope.SetMessageType<T>` closed-generic
- `Envelope.SetMetricsTag`
- Envelope scheduling helpers (`ScheduleAt`, `ScheduleDelayed`, `IsScheduledForLater`, `IsExpired`)
- `DeliveryOptions` construction
- `WolverineOptions` construction
- `WolverineOptions.DetermineSerializer` (dictionary lookup)

As the per-file annotation pass in #2746 progresses, the smoke project expands to exercise the newly-annotated entry points — tightening the regression guard incrementally.

## Surfaces that intentionally aren't AOT-clean

A handful of Wolverine APIs **require** runtime codegen by design. They carry `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` annotations so the trimmer surfaces a clear warning if your AOT-published app reaches them:

- `HandlerGraph.Compile` and its callees — runtime Roslyn codegen of handler dispatch. Static-mode apps don't reach this.
- Reflective handler / saga / transport discovery (`HandlerDiscovery.IncludeAssembly` type-scan, the saga-type-descriptor `StartingMessages` reflection, transport `IMessageRoutingConvention` reflection). Static-mode + a pre-generated handler manifest avoid these.
- `MakeGenericType` / `Activator.CreateInstance` driven factories — the few that still exist in core.
- The `WolverineFx.Newtonsoft` companion package (Newtonsoft.Json itself isn't AOT-friendly; if you need Newtonsoft, you're not on the AOT path).

If you publish AOT in `Dynamic` mode (the default), expect warnings from these surfaces — they tell you what to migrate before you can ship a working AOT binary.

## Cold-start side benefit

Even without publishing AOT, `TypeLoadMode.Static` produces meaningful cold-start improvements: the host doesn't pay the per-handler Roslyn compile cost on first message. The [`CritterStackScalability`](https://github.com/JasperFx/CritterStackScalability) `coldstart wolverine` harness measures this against the V5.39.0 baseline.

## Migration checklist

If you're moving an existing 5.x Wolverine app to 6.0 AOT:

1. Upgrade to Wolverine 6.0 (see the [migration guide](/guide/migration.html))
2. Run `dotnet run -- codegen write` locally; verify the `Internal/Generated/` folder gets populated and the files compile
3. Add `opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;` to your `UseWolverine` configuration
4. Run your existing test suite — same behavior, just no runtime codegen
5. Add `/p:PublishAot=true` to your `dotnet publish` command, fix any remaining IL warnings (or accept them with `<NoWarn>`) and ship

If a handler type changes between releases, re-run `codegen write` and commit the regenerated files. Add the regeneration step to your CI pipeline (or pre-commit hook) so the pre-generated code never drifts from the source handlers.

## Related

- [Code Generation](/guide/codegen.html) — full reference for `TypeLoadMode`, `codegen write`, and the underlying JasperFx runtime-compilation surface
- [`Wolverine.AotSmoke`](https://github.com/JasperFx/wolverine/tree/main/src/Testing/Wolverine.AotSmoke) — the regression-guard project this guide references
- [AOT-pillar tracking](https://github.com/JasperFx/wolverine/issues/2746) — what's annotated, what's left
- [JasperFx 2.0 AOT pillar](https://github.com/JasperFx/jasperfx/issues/213) — the foundation Wolverine builds on
