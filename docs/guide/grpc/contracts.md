# Code-First and Proto-First Contracts

Wolverine supports both idiomatic ways of defining a gRPC surface on .NET:

- **Code-first** — a C# interface with `[ServiceContract]`, using
  [protobuf-net.Grpc](https://protobuf-net.github.io/protobuf-net.Grpc/) to synthesise the wire
  format at runtime.
- **Proto-first** — a `.proto` file compiled by `Grpc.Tools` into a `{Service}Base` stub.

They can coexist in the same host — `AddCodeFirstGrpc()` and `AddGrpc()` are orthogonal. The table
below is the quick decision lens; the rest of the page has both sides laid out together so you can
scroll between them and compare.

## Picking a style

| Decision point | Code-first | Proto-first |
|---|---|---|
| All callers are .NET | Fine | Fine |
| You need a polyglot contract (Go/Python/mobile clients) | Possible but unusual | Preferred |
| You want types that live only in C# | ✅ | Awkward (you'd re-declare in `.proto`) |
| You want a contract your consumers can `buf breaking`-check | Not directly | ✅ |
| You can regenerate code on every build | Optional | ✅ Required (`Grpc.Tools`) |
| Streaming shape is expressed in… | The method signature (`IAsyncEnumerable<T>`) | The `.proto` (`stream`) |
| Runtime dependency | `protobuf-net.Grpc` | `Grpc.AspNetCore` + generated bindings |

A common pragmatic split: **code-first for internal service-to-service calls** where both ends
always deploy together, **proto-first for anything the outside world consumes**.

## Code-first codegen (generated implementation)

Applying `[WolverineGrpcService]` to the **interface** itself alongside `[ServiceContract]` lets
Wolverine generate the concrete implementation for you. Wolverine discovers the interface at startup,
emits `{ServiceName}GrpcHandler` that injects `IMessageBus`, and maps it — no service class to write
or maintain.

```csharp
[ServiceContract]
[WolverineGrpcService]
public interface IGreeterCodeFirstService
{
    Task<GreetReply> Greet(GreetRequest request, CallContext context = default);
    IAsyncEnumerable<GreetReply> StreamGreetings(StreamGreetingsRequest request, CallContext context = default);
}

// That's the whole contract. No service class needed.

// Ordinary Wolverine handlers — no gRPC coupling
public static class GreeterHandler
{
    public static GreetReply Handle(GreetRequest request)
        => new() { Message = $"Hello, {request.Name}!" };

    public static async IAsyncEnumerable<GreetReply> Handle(
        StreamGreetingsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new GreetReply { Message = $"Hello, {request.Name} [{i} of {request.Count}]" };
            await Task.Yield();
        }
    }
}
```

Bootstrap is the same as the hand-written path, with one addition: if the annotated interface lives
in a separate assembly from the server (e.g. a shared `Messages` project), tell Wolverine to scan
that assembly so `GrpcGraph` can find it:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
    opts.Discovery.IncludeAssembly(typeof(IGreeterCodeFirstService).Assembly);
});

builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

// ...
app.MapWolverineGrpcServices();
```

The generated class name follows the convention `{InterfaceNameWithoutLeadingI}GrpcHandler` — so
`IGreeterCodeFirstService` → `GreeterCodeFirstServiceGrpcHandler`. The generated type implements the
contract interface and is the class that protobuf-net.Grpc maps as the service endpoint.

On the client side, use the same interface with `channel.CreateGrpcService<T>()` — no stubs, no
`.proto` file, no code-gen step:

```csharp
using var channel = GrpcChannel.ForAddress("https://greeter.example");
var greeter = channel.CreateGrpcService<IGreeterCodeFirstService>();

var reply = await greeter.Greet(new GreetRequest { Name = "Erik" });
await foreach (var item in greeter.StreamGreetings(new StreamGreetingsRequest { Name = "Erik", Count = 5 }))
    Console.WriteLine(item.Message);
```

The [GreeterCodeFirstGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterCodeFirstGrpc)
sample demonstrates this end-to-end. See [Samples](./samples#greetercodefirstgrpc) for a walkthrough.

::: warning No conflict allowed
`[WolverineGrpcService]` must appear on **either** the interface **or** a concrete implementing
class — not both. If Wolverine finds the attribute on both, it throws `InvalidOperationException`
at startup with a diagnostic identifying the conflict. This mirrors the proto-first rule that the
stub must be abstract.
:::

## Unary RPC

### Code-first (hand-written service class)

When you prefer explicit control over the service class — for example to add per-method logging or
to call multiple downstream services from one RPC — write the class yourself:

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

Wolverine generates a thin **delegation wrapper** around the class at startup (named
`{ClassName}GrpcHandler`). The wrapper implements the same `[ServiceContract]` interface, weaves
any `Validate` / `[WolverineBefore]` middleware defined on the service class, then calls into the
inner class — which Wolverine resolves from the DI container or constructs via
`ActivatorUtilities` if no explicit registration exists. This gives hand-written service classes
the same middleware and validation hooks available to the proto-first and generated-implementation
paths.

### Proto-first

```proto
// ping.proto
service Ping {
    rpc Ping (PingRequest) returns (PongReply);
}
```

```csharp
[WolverineGrpcService]
public abstract class PingGrpcService : Ping.PingBase;   // <- abstract is required

public static class PingHandler
{
    public static PongReply Handle(PingRequest request) => new() { Echo = request.Message };
}
```

At startup Wolverine scans assemblies, finds the abstract stub, and generates a concrete wrapper
named `{ProtoServiceName}GrpcHandler` (e.g. `PingGrpcHandler`) that overrides each RPC and forwards
to `IMessageBus`. The generated type participates in the same code-generation pipeline as handler
and HTTP chains — it shows up in `dotnet run -- describe` and `describe-routing` CLI diagnostics.

::: warning
The stub **must be abstract**. Making it concrete short-circuits the code-generation pipeline —
Wolverine throws `InvalidOperationException` with a diagnostic message pointing at the offending
type. Proto-first handlers must live on separate classes, not on the stub itself.
:::

## Server streaming

Same handler shape on both sides — `IAsyncEnumerable<T>` plus `[EnumeratorCancellation]`. The
difference is purely in how the contract is declared. For the generated-implementation path, the
interface method simply returns `IAsyncEnumerable<T>` — see the [Code-first codegen](#code-first-codegen-generated-implementation)
section above.

### Code-first (hand-written service class)

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

### Proto-first

```proto
// greeter.proto
service Greeter {
    rpc SayHello (HelloRequest) returns (HelloReply);
    rpc StreamGreetings (StreamGreetingsRequest) returns (stream HelloReply);
}
```

```csharp
[WolverineGrpcService]
public abstract class GreeterGrpcService : Greeter.GreeterBase;

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

Notice that the handler is identical between code-first and proto-first — only the contract
declaration changes. Cancellation from the client propagates into the handler's `CancellationToken`
in both styles, so mid-stream cancellation cleanly unwinds. For the broader streaming story
(bidirectional, limitations, timing) see [Streaming](./streaming).

## Mixing both in one host

Nothing stops you from running both styles together. The registration order doesn't matter — call
`AddCodeFirstGrpc()` and `AddGrpc()` and `AddWolverineGrpc()` in any order, then the single
`MapWolverineGrpcServices()` call handles both:

```csharp
builder.Services.AddCodeFirstGrpc();   // protobuf-net.Grpc
builder.Services.AddGrpc();            // Grpc.AspNetCore — proto-first
builder.Services.AddWolverineGrpc();

// ...
app.MapWolverineGrpcServices();        // discovers code-first + proto-first in one pass
```

This is what the `GreeterProtoFirstGrpc` sample does implicitly — Wolverine sits in front and
routes correctly regardless of which contract style produced the service.

## Wiring recap

Whichever style you pick, the handler is an ordinary Wolverine handler. The gRPC service is a
forwarder; the contract type is the thing that changes. If you haven't yet, [How gRPC Handlers
Work](./handlers) walks through the full service → bus → handler flow.
