# gRPC Services with Wolverine

::: info
The `WolverineFx.Grpc` package (experimental, shipping alongside gRPC support on the
`feature/grpc-and-streaming-support` branch) lets you expose Wolverine handlers as
ASP.NET Core gRPC services with minimal wiring. It supports both the **code-first**
([protobuf-net.Grpc](https://protobuf-net.github.io/protobuf-net.Grpc/)) and **proto-first**
([Grpc.Tools](https://learn.microsoft.com/en-us/aspnet/core/grpc/)) styles.
:::

## Why gRPC?

If you're already using Wolverine to orchestrate message handlers, sagas, and HTTP endpoints,
gRPC gives you another edge protocol for the same handlers. Benefits:

- **Strongly-typed contracts** shared across .NET and non-.NET services via `.proto` files, or code-first
  contracts that never leave C#.
- **Streaming** first-class — plays naturally with Wolverine's [`IMessageBus.StreamAsync<T>`](/guide/messaging/message-bus.html#streaming-responses).
- **Wolverine handler reuse** — the same handler can back a REST endpoint, an async message, and a
  gRPC call without duplication.
- **Canonical error semantics** — ordinary .NET exceptions thrown by a handler are mapped to the
  right gRPC `StatusCode` automatically, following [Google AIP-193](https://google.aip.dev/193).

## What's in this section

Start here to get Wolverine's gRPC adapter running, then drill into the page that matches what you're
building:

- [How gRPC Handlers Work](./handlers) — the service → `IMessageBus` → handler flow, how it differs
  from HTTP and messaging handlers, and how OpenTelemetry traces survive the hop.
- [Code-First and Proto-First Contracts](./contracts) — the two contract styles side by side so you
  can pick (or mix) them.
- [Error Handling](./errors) — the default AIP-193 exception → `StatusCode` table plus the opt-in
  `google.rpc.Status` pipeline for rich, structured details.
- [Streaming](./streaming) — server streaming today, bidirectional via a manual bridge, and the
  shape of the cancellation contract.
- [Typed gRPC Clients](./client) — `AddWolverineGrpcClient<T>()`, the Wolverine-flavored wrapper over
  `Grpc.Net.ClientFactory` that adds envelope-header propagation and `RpcException` → typed-exception
  translation on the consuming side.
- [Samples](./samples) — runnable end-to-end samples with pointers to the equivalent official
  [grpc-dotnet examples](https://github.com/grpc/grpc-dotnet/tree/master/examples) for comparison.

## Getting Started

Add the integration package and register it alongside the usual Wolverine bootstrap. `AddWolverineGrpc`
does **not** register a gRPC host — callers decide whether they want code-first or proto-first (or both):

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Pick one (or both):
builder.Services.AddCodeFirstGrpc();   // protobuf-net.Grpc — code-first
builder.Services.AddGrpc();            // Grpc.AspNetCore — proto-first

// Wolverine's gRPC adapter (exception interceptor, discovery, codegen pipeline)
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();

// Discovers every '*GrpcService' / [WolverineGrpcService] type and maps it for you.
// Proto-first stubs generate a concrete wrapper on the fly.
app.MapWolverineGrpcServices();

app.Run();
```

From here, [How gRPC Handlers Work](./handlers) walks through what `MapWolverineGrpcServices` actually
wires up and why a gRPC handler is just an ordinary Wolverine handler with a thin service shim on top.

::: tip Runnable Samples
Five end-to-end sample trios live under `src/Samples/`. Each pairs a real Kestrel HTTP/2 server
with a client in separate projects so you can `dotnet run` them side by side. See
[Samples](./samples) for the full walkthroughs and comparisons to the official `grpc-dotnet` examples.

| Sample | Shape | What to copy |
|--------|-------|--------------|
| [PingPongWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpc)                     | Code-first **unary** | `[ServiceContract]` + `WolverineGrpcServiceBase` forwarding to a plain handler |
| [PingPongWithGrpcStreaming](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpcStreaming)   | Code-first **server streaming** | Handler returning `IAsyncEnumerable<T>`, forwarded via `Bus.StreamAsync<T>` |
| [GreeterProtoFirstGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterProtoFirstGrpc)           | **Proto-first** unary + server streaming + exception mapping | Abstract `[WolverineGrpcService]` stub subclassing a generated `*Base` + handlers |
| [RacerWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerWithGrpc)                           | Code-first **bidirectional streaming** | Per-update bridge: client `IAsyncEnumerable<TReq>` → `Bus.StreamAsync<TResp>` for each item |
| [GreeterWithGrpcErrors](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterWithGrpcErrors)           | Code-first **rich error details** | FluentValidation → `BadRequest` plus inline `MapException` → `PreconditionFailure`, with a client that unpacks both |
:::

## API Reference

| Type / Member                              | Purpose                                                           |
|--------------------------------------------|-------------------------------------------------------------------|
| `AddWolverineGrpc()`                       | Registers the interceptor, proto-first discovery graph, and codegen pipeline. |
| `MapWolverineGrpcServices()`               | Discovers and maps all gRPC services (code-first and proto-first). |
| `WolverineGrpcServiceBase`                 | Optional base class exposing an `IMessageBus` property `Bus`.     |
| `[WolverineGrpcService]`                   | Opt-in marker for classes that don't match the `GrpcService` suffix. |
| `WolverineGrpcExceptionMapper.Map(ex)`     | The public mapping table — use directly in custom interceptors.   |
| `WolverineGrpcExceptionInterceptor`        | The registered gRPC interceptor; exposed for diagnostics.         |
| `opts.UseGrpcRichErrorDetails(...)`        | Opt-in `google.rpc.Status` pipeline — see [Error Handling](./errors). |
| `opts.UseFluentValidationGrpcErrorDetails()` | Bridge: `ValidationException` → `BadRequest` (from `WolverineFx.FluentValidation.Grpc`). |
| `IGrpcStatusDetailsProvider`               | Custom provider seam for building `google.rpc.Status` from an exception. |
| `IValidationFailureAdapter`                | Plug-in point for translating library-specific validation exceptions into `BadRequest.FieldViolation`s. |
| `AddWolverineGrpcClient<T>()`              | Registers a Wolverine-flavored typed gRPC client — see [Typed gRPC Clients](./client). |
| `WolverineGrpcClientOptions`               | Named options for a typed client: `Address`, `PropagateEnvelopeHeaders`, `MapRpcException`. |
| `WolverineGrpcExceptionMapper.MapToException(rpc)` | Inverse of `Map` — client-side `RpcException` → typed .NET exception table. |

## Current Limitations

- **Client streaming** and **bidirectional streaming** have no out-of-the-box adapter path yet —
  there is no `IMessageBus.StreamAsync<TRequest, TResponse>` overload, and proto-first stubs with
  these method shapes fail fast at startup with a clear error rather than silently skipping. In
  code-first you can still implement bidi manually in the service by bridging each incoming item
  through `Bus.StreamAsync<TResp>(item, ct)` — see [Streaming](./streaming) for the pattern and the
  [RacerWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerWithGrpc) sample.
- **Exception mapping** of the canonical `Exception → StatusCode` table is not yet user-configurable
  (follow-up item). Rich, structured responses are already available — see
  [Error Handling](./errors).

## Roadmap

The gRPC integration is intentionally shipping as a focused, reviewable slice. The items below are
on the roadmap but *not* in the initial drop — they're listed here so contributors can plan around
them and consumers know what's coming.

### Shipping in this PR

- **`MiddlewareScoping.Grpc`** — the existing [scoped middleware](/guide/handlers/middleware#applying-middleware-explicitly-by-attribute)
  enum grows a `Grpc` value so gRPC-specific middleware can be registered the same way HTTP and
  messaging middleware already are. Previously, gRPC service chains reported themselves as
  `MessageHandlers`-scoped, which silently over-attached message middleware to gRPC calls. This is
  a behavior correction, not an additive feature.
- **`codegen-preview --grpc`** — the [`codegen-preview` CLI](/guide/command-line#codegen-preview)
  grows a `--grpc` / `-g` flag (mirroring `--handler` / `-h` and `--route` / `-r`) so you can inspect
  the code Wolverine generates for gRPC service chains without dropping into the full `codegen write`
  output.
- **Typed gRPC client extension (`AddWolverineGrpcClient<T>()`)** — a thin Wolverine wrapper over
  `Grpc.Net.ClientFactory.AddGrpcClient<T>()` that layers envelope-header propagation and
  `RpcException` → typed .NET exception translation onto both code-first (`protobuf-net.Grpc`
  `[ServiceContract]`) and proto-first (`Grpc.Tools`-generated) typed clients. See
  [Typed gRPC Clients](./client) for the full surface. Raw `GrpcChannel` + generated stubs (as used
  in the samples today) remain a fully supported path.

### Deferred to follow-up PRs

- **`Validate` convention → `Status?`** — HTTP handlers already support an opt-in `Validate` method
  whose non-null return short-circuits the call. The gRPC equivalent would return
  `Grpc.Core.Status?` (or a richer `google.rpc.Status`) so a handler could express "this call is
  invalid, return `InvalidArgument` with these field violations" without throwing. This is
  deferred because it lands cleanest on top of the code-first codegen work below — shipping it
  against the current runtime path would bake in assumptions we'll want to revisit.
- **Code-first codegen parity** — proto-first services flow through a generated `GrpcServiceChain`
  with the usual JasperFx codegen pipeline; code-first services (the `WolverineGrpcServiceBase` path)
  currently resolve dependencies via service location inside each method. Generating per-method code
  files for code-first services — matching the HTTP and message handler story — is the prerequisite
  for the `Validate` convention above and for tighter Lamar/MSDI optimization.
- **Hybrid handler shape (HTTP + gRPC + messaging on one type)** — open design question. The
  [hybrid HTTP/message handler](/guide/http/endpoints#http-endpoint-message-handler-combo) pattern
  works today for two protocols; extending it to three raises naming and scoping questions
  (`MiddlewareScoping` only permits one value per `[Middleware]` attribute, and the handler method
  name conventions overlap). No concrete plan yet — feedback welcome on the tracking issue.
