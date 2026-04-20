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

## Unary RPC

### Code-first

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
difference is purely in how the contract is declared.

### Code-first

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
