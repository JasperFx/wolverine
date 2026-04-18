# Samples

Five end-to-end sample trios (server, client, shared messages) live under
[`src/Samples/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples). Each one is a real
Kestrel HTTP/2 host plus a separate client project — `dotnet run` them side by side.

For each Wolverine sample below you'll find a pointer to the **closest equivalent** in the official
[grpc-dotnet examples](https://github.com/grpc/grpc-dotnet/tree/master/examples), so you can
contrast how the same shape looks with and without Wolverine's handler pipeline.

::: tip How to read the comparisons
The official grpc-dotnet examples put business logic **inside** the gRPC service method (an
override on `{Service}Base`). Wolverine's samples instead keep the service method as a one-line
forwarder and put the logic in a plain Wolverine handler. The wire protocol is identical — what
changes is whether your business code knows about gRPC.
:::

## Overview

| Wolverine sample | Shape | Style | Closest grpc-dotnet example |
|---|---|---|---|
| [PingPongWithGrpc](#pingpongwithgrpc)                     | Unary                | Code-first  | [Greeter](https://github.com/grpc/grpc-dotnet/tree/master/examples#greeter) |
| [PingPongWithGrpcStreaming](#pingpongwithgrpcstreaming)   | Server streaming     | Code-first  | [Counter](https://github.com/grpc/grpc-dotnet/tree/master/examples#counter) |
| [GreeterProtoFirstGrpc](#greeterprotofirstgrpc)           | Unary + server streaming + exception mapping | Proto-first | [Greeter](https://github.com/grpc/grpc-dotnet/tree/master/examples#greeter) |
| [RacerWithGrpc](#racerwithgrpc)                           | Bidirectional streaming | Code-first | [Racer](https://github.com/grpc/grpc-dotnet/tree/master/examples#racer) |
| [GreeterWithGrpcErrors](#greeterwithgrpcerrors)           | Unary + rich error details | Code-first | (no direct equivalent — closest is Greeter + a custom interceptor) |

## PingPongWithGrpc

Layout: [`src/Samples/PingPongWithGrpc/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpc)
with three projects — `Messages`, `Ponger` (server), `Pinger` (client).

The minimal unary case. A `Pinger` client sends a `PingRequest`; the `Ponger` server replies with a
`PongReply`. The gRPC service is **one line** because the handler does all the work:

```csharp
public Task<PongReply> Ping(PingRequest request, CallContext context = default)
    => Bus.InvokeAsync<PongReply>(request, context.CancellationToken);
```

The handler (`public static PongReply Handle(PingRequest)`) lives in the server project and knows
nothing about gRPC — it's exactly the same handler you'd write for messaging or HTTP.

**What to copy**: `[ServiceContract]` + `WolverineGrpcServiceBase` with a one-line forward to
`Bus.InvokeAsync<T>`. This is the canonical pattern for every code-first unary RPC.

### Compared to grpc-dotnet's Greeter

grpc-dotnet's **Greeter** example overrides `Greeter.GreeterBase.SayHello` and returns the
`HelloReply` directly from the override. Both approaches produce the same HTTP/2 frames on the
wire. The difference:

- grpc-dotnet Greeter: business code is on the gRPC service class.
- Wolverine PingPong: business code is on a plain handler; the gRPC service is a forwarder.

That separation is what lets the same handler back HTTP, messaging, and gRPC surfaces
simultaneously — Wolverine's core value proposition applied to gRPC.

## PingPongWithGrpcStreaming

Layout: [`src/Samples/PingPongWithGrpcStreaming/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpcStreaming).

Same shape as PingPong, but the server emits a stream of replies. The handler returns
`IAsyncEnumerable<PongReply>`; the service shim forwards through `Bus.StreamAsync<T>`:

```csharp
public IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default)
    => Bus.StreamAsync<PongReply>(request, context.CancellationToken);
```

**What to copy**: the three-part trio of `[ServiceContract]` returning `IAsyncEnumerable<T>`, the
one-line `Bus.StreamAsync<T>` forward, and a handler with `[EnumeratorCancellation]`. See
[Streaming](./streaming) for the full pattern.

### Compared to grpc-dotnet's Counter

grpc-dotnet's **Counter** example overrides `Counter.CounterBase.Count(Request, responseStream,
context)`, writing to the `IServerStreamWriter<T>` inside a loop. The Wolverine sample's handler
is a plain `async IAsyncEnumerable<T> Handle(...)` method — no `IServerStreamWriter<T>` reference,
no `WriteAsync` call. Wolverine's adapter does the `WriteAsync` for you under the hood.

The upshot: the exact same handler can also be invoked in-process via `IMessageBus.StreamAsync<T>`
for testing or for non-gRPC consumers.

## GreeterProtoFirstGrpc

Layout: [`src/Samples/GreeterProtoFirstGrpc/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterProtoFirstGrpc)
with `Messages`, `Server`, `Client`.

A proto-first sample that exercises three things at once: unary RPC, server streaming, and the
default exception → `StatusCode` mapping. The `.proto` declares `SayHello` (unary) and
`StreamGreetings` (server streaming); Grpc.Tools generates `Greeter.GreeterBase`; a single line
hands the rest to Wolverine:

```csharp
[WolverineGrpcService]
public abstract class GreeterGrpcService : Greeter.GreeterBase;
```

That's the whole service. Wolverine generates the concrete `GreeterGrpcHandler` wrapper at
startup, and the `Handle` methods on `GreeterHandler` supply the behaviour.

**What to copy**: the abstract-stub pattern for proto-first, plus how throwing
`ArgumentException` / `KeyNotFoundException` / `InvalidOperationException` from a handler yields
the matching gRPC `StatusCode` on the client (see [Error Handling](./errors)).

### Compared to grpc-dotnet's Greeter

grpc-dotnet's Greeter is also proto-first, and it's the textbook comparison point. The delta:

- grpc-dotnet: `public class GreeterService : Greeter.GreeterBase { override Task<HelloReply> SayHello(...) { ... } }` — override is where the logic lives.
- Wolverine: abstract stub + plain `GreeterHandler.Handle`. No override, no gRPC types in the handler.

Additionally, the Wolverine sample demonstrates the exception-mapping layer — throwing a regular
.NET exception and watching the client receive the correct `RpcException.StatusCode`. The
grpc-dotnet Greeter doesn't show this because there's no handler pipeline to map from.

## RacerWithGrpc

Layout: [`src/Samples/RacerWithGrpc/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerWithGrpc)
with `RacerContracts`, `RacerServer`, `RacerClient`.

Bidirectional streaming via a manual per-item bridge. The client sends a stream of race commands;
for each command, the server streams back race updates. The bridge pattern:

```csharp
public async IAsyncEnumerable<RaceUpdate> Race(
    IAsyncEnumerable<RaceCommand> incoming,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var command in incoming.WithCancellation(cancellationToken))
    {
        await foreach (var update in Bus.StreamAsync<RaceUpdate>(command, cancellationToken))
        {
            yield return update;
        }
    }
}
```

**What to copy**: the "one Wolverine stream per incoming command" shape. It's the path to bidi
today until `IMessageBus.StreamAsync<TRequest, TResponse>` lands.

### Compared to grpc-dotnet's Racer

grpc-dotnet's **Racer** is also bidi and is where this sample's name comes from. Differences:

- grpc-dotnet Racer: reads `requestStream` and writes `responseStream` in parallel inside the
  service override, driving both sides directly.
- Wolverine RacerWithGrpc: reads the request stream, fans each item into a server-streaming
  Wolverine call, and re-emits the updates.

The Wolverine version makes sense when **each incoming message has its own reactive stream of
responses** (start → progress → finished). If your bidi is truly peer-to-peer with no per-item
correlation, grpc-dotnet's direct pattern may be a better fit — or, better yet, a Wolverine saga
with outbound streaming.

## GreeterWithGrpcErrors

Layout: [`src/Samples/GreeterWithGrpcErrors/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterWithGrpcErrors)
with `Messages`, `Server`, `Client`, and a `README.md` walking through both error paths.

The "rich errors" showcase. Demonstrates both halves of the `google.rpc.Status` pipeline:

1. **Validation → `BadRequest`**: a FluentValidation rule on the request DTO fails; the
   `UseFluentValidationGrpcErrorDetails()` bridge turns each `ValidationFailure` into a
   `BadRequest.FieldViolation` on the wire.
2. **Domain exception → `PreconditionFailure`**: an inline
   `MapException<GreetingForbiddenException>(...)` maps a policy-violation exception to
   `StatusCode.FailedPrecondition` with a structured `PreconditionFailure.Violation` attached.

The client catches `RpcException`, calls `GetRpcStatus()`, and unpacks each `Any` to render the
field-level errors or the precondition violation.

**What to copy**: the full opt-in pipeline — `UseGrpcRichErrorDetails` +
`UseFluentValidationGrpcErrorDetails` + inline `MapException` + client-side `GetRpcStatus` /
`Any.Unpack<T>`. Mirrors how you'd wire `ProblemDetails` / `ValidationProblemDetails` on the HTTP
side.

### Compared to grpc-dotnet examples

There's no direct equivalent in the grpc-dotnet repo — the official examples demonstrate `Status`
via a **custom interceptor** that the service author writes from scratch. The Wolverine sample
shows the same outcome with no custom interceptor code in the service project: the opt-in
pipeline, a bridge package, and per-exception declarative mapping do the work.

## Related

- [Index](./) — overview + getting started.
- [How gRPC Handlers Work](./handlers) — why the samples can keep their service classes tiny.
- [Code-First and Proto-First Contracts](./contracts) — pick the style each sample uses.
- [Streaming](./streaming) — the streaming shapes two of these samples implement.
- [Error Handling](./errors) — the pipeline `GreeterWithGrpcErrors` exercises end-to-end.
