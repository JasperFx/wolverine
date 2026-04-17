# gRPC Services with Wolverine

::: info
The `WolverineFx.Http.Grpc` package (experimental, shipping alongside gRPC support on the
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

::: tip Runnable Samples
Four end-to-end sample trios live under `src/Samples/`. Each pairs a real Kestrel HTTP/2 server
with a client in separate projects so you can `dotnet run` them side by side:

| Sample | Shape | What to copy |
|--------|-------|--------------|
| [PingPongWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpc)                     | Code-first **unary** | `[ServiceContract]` + `WolverineGrpcServiceBase` forwarding to a plain handler |
| [PingPongWithGrpcStreaming](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpcStreaming)   | Code-first **server streaming** | Handler returning `IAsyncEnumerable<T>`, forwarded via `Bus.StreamAsync<T>` |
| [GreeterProtoFirstGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterProtoFirstGrpc)           | **Proto-first** unary + server streaming + exception mapping | Abstract `[WolverineGrpcService]` stub subclassing a generated `*Base` + handlers |
| [RacerWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerWithGrpc)                           | Code-first **bidirectional streaming** | Per-update bridge: client `IAsyncEnumerable<TReq>` → `Bus.StreamAsync<TResp>` for each item |
:::

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

## Code-First Services

In the code-first style you define a .NET interface decorated with `[ServiceContract]`.
Implementations inherit `WolverineGrpcServiceBase` to get a pre-wired `IMessageBus`, then forward
each method to `Bus.InvokeAsync<T>` or `Bus.StreamAsync<T>`.

### Unary RPC

```csharp
[ServiceContract]
public interface IPingService
{
    Task<PongReply> Ping(PingRequest request, CallContext context = default);
}

public class PingGrpcService : WolverineGrpcServiceBase, IPingService
{
    public PingGrpcService(IMessageBus bus) : base(bus) { }

    public Task<PongReply> Ping(PingRequest request, CallContext context = default)
        => Bus.InvokeAsync<PongReply>(request, context.CancellationToken);
}

// Ordinary Wolverine handler — no gRPC coupling
public static class PingHandler
{
    public static PongReply Handle(PingRequest request) => new() { Echo = request.Message };
}
```

Any class whose name ends in `GrpcService` is picked up by `MapWolverineGrpcServices()`. If the
suffix convention doesn't fit, apply `[WolverineGrpcService]` instead.

### Server Streaming

Return `IAsyncEnumerable<T>` from the contract method. protobuf-net.Grpc recognises the return type
as a server-streaming RPC and wires the transport for you.

```csharp
[ServiceContract]
public interface IPingStreamService
{
    IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default);
}

public class PingStreamGrpcService : WolverineGrpcServiceBase, IPingStreamService
{
    public PingStreamGrpcService(IMessageBus bus) : base(bus) { }

    public IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default)
        => Bus.StreamAsync<PongReply>(request, context.CancellationToken);
}

public static class PingStreamHandler
{
    public static async IAsyncEnumerable<PongReply> Handle(
        PingStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new PongReply { Echo = $"{request.Message}:{i}" };
            await Task.Yield();
        }
    }
}
```

Cancellation from the client propagates into the handler's `CancellationToken`, so mid-stream
cancellation cleanly unwinds.

## Proto-First Services

In proto-first mode you ship a `.proto` file, let `Grpc.Tools` generate the `{Service}Base` stub,
and mark an abstract subclass with `[WolverineGrpcService]`:

```proto
// greeter.proto
service Greeter {
    rpc SayHello (HelloRequest) returns (HelloReply);
    rpc StreamGreetings (StreamGreetingsRequest) returns (stream HelloReply);
}
```

```csharp
[WolverineGrpcService]
public abstract class GreeterGrpcService : Greeter.GreeterBase;  // <- abstract is required

public static class GreeterHandler
{
    public static HelloReply Handle(HelloRequest request)
        => new() { Message = $"Hello, {request.Name}" };

    public static async IAsyncEnumerable<HelloReply> Handle(
        StreamGreetingsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new HelloReply { Message = $"Hello, {request.Name} [{i}]" };
            await Task.Yield();
        }
    }
}
```

At startup, Wolverine scans assemblies, finds the abstract stub, and generates a concrete wrapper
named `{ProtoServiceName}GrpcHandler` (e.g. `GreeterGrpcHandler`) that forwards each RPC to
`IMessageBus`. The generated type participates in the same code-generation pipeline as handler and
HTTP chains — it shows up in `dotnet run -- describe` and `describe-routing` CLI diagnostics.

::: warning
The stub **must be abstract**. Making it concrete short-circuits the code-generation pipeline —
Wolverine throws `InvalidOperationException` with a diagnostic message pointing at the offending
type. Proto-first handlers must live on separate classes, not on the stub itself.
:::

## Exception Handling (AIP-193)

`WolverineGrpcExceptionInterceptor` is registered automatically by `AddWolverineGrpc` and applies to
both code-first and proto-first services. It translates ordinary .NET exceptions thrown by handlers
into `RpcException` with the canonical status code from the table below:

| Exception                     | gRPC Status Code        |
|-------------------------------|-------------------------|
| `OperationCanceledException`  | `Cancelled`             |
| `TimeoutException`            | `DeadlineExceeded`      |
| `ArgumentException` (& subclasses) | `InvalidArgument`  |
| `KeyNotFoundException`        | `NotFound`              |
| `FileNotFoundException`       | `NotFound`              |
| `DirectoryNotFoundException`  | `NotFound`              |
| `UnauthorizedAccessException` | `PermissionDenied`      |
| `InvalidOperationException`   | `FailedPrecondition`    |
| `NotImplementedException`     | `Unimplemented`         |
| `NotSupportedException`       | `Unimplemented`         |
| `RpcException`                | *(preserved as-is)*     |
| *anything else*               | `Internal`              |

This means a handler can just throw `KeyNotFoundException` and the gRPC client will receive an
`RpcException` with `StatusCode.NotFound` — no explicit translation layer in every service method.

```csharp
public static OrderReply Handle(GetOrder request, IOrderStore store)
{
    var order = store.Find(request.OrderId)
        ?? throw new KeyNotFoundException($"Order {request.OrderId}");  // → NotFound
    return new OrderReply { /* ... */ };
}
```

::: info
A user-configurable error policy and opt-in `google.rpc.Status` rich error details are planned as
a follow-up. Until then, throwing `RpcException` directly is the escape hatch when you need a status
code or trailer that isn't in the default table.
:::

## Observability

Wolverine's gRPC adapter preserves `Activity.Current` across the boundary between the ASP.NET Core
gRPC pipeline and the Wolverine handler pipeline. Concretely:

- ASP.NET Core's hosting diagnostics starts an inbound activity (`Microsoft.AspNetCore.Hosting.HttpRequestIn`)
  and extracts any W3C `traceparent` header the client sent.
- The gRPC service method (code-first or proto-first generated wrapper) invokes
  `IMessageBus.InvokeAsync` / `IMessageBus.StreamAsync` on the same ExecutionContext, so
  `Activity.Current` is still the hosting activity when Wolverine starts its own handler span.
- Wolverine's `WolverineTracing.StartExecuting` inherits `Activity.Current` as parent, which means
  every handler activity lives under the same TraceId as the inbound gRPC request.

The result: a single trace covers the full request, from inbound gRPC to every Wolverine handler it
invokes, with no additional wiring. If you expose an OpenTelemetry pipeline for other Wolverine
transports, it will pick up gRPC traffic for free.

### Registering OpenTelemetry

Add the `"Wolverine"` ActivitySource alongside the ASP.NET Core and gRPC sources:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("Wolverine")                  // Wolverine handler activities
        .AddAspNetCoreInstrumentation()          // ASP.NET Core hosting activities
        .AddGrpcClientInstrumentation()          // outbound gRPC calls
        .AddOtlpExporter());
```

See [Instrumentation and Metrics](/guide/logging) for the full set of Wolverine ActivitySource tags
and semantic conventions.

::: warning
Cross-process traceparent propagation depends on real HTTP/2 — it goes through `HttpClient`'s
diagnostic handler on the client and ASP.NET Core hosting diagnostics on the server. Integration
tests based on `Microsoft.AspNetCore.TestHost.TestServer` bypass that layer, so the client's
TraceId will not reach the server in tests even though it does in production. Use real hosts or
`WebApplicationFactory` with a loopback port if you need to assert end-to-end propagation.
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

## Current Limitations

- **Client streaming** and **bidirectional streaming** have no out-of-the-box adapter path yet —
  there is no `IMessageBus.StreamAsync<TRequest, TResponse>` overload, and proto-first stubs with
  these method shapes fail fast at startup with a clear error rather than silently skipping. In
  code-first you can still implement bidi manually in the service by bridging each incoming item
  through `Bus.StreamAsync<TResp>(item, ct)` — see the
  [RacerWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerWithGrpc) sample.
- **Exception mapping** is not yet user-configurable (follow-up item).
- **Rich error details** (`google.rpc.Status` trailers) are deferred.
