# Samples

Eight end-to-end samples live under
[`src/Samples/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples). Most follow the
classic trio shape (server, client, shared messages); `OrderChainWithGrpc` is a quartet because its
proof-point is a **chain** between two Wolverine servers. Each sample is a real Kestrel HTTP/2
host plus separate client / service projects — `dotnet run` them side by side.

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
| [PingPongWithGrpc](#pingpongwithgrpc)                     | Unary                | Code-first (hand-written)  | [Greeter](https://github.com/grpc/grpc-dotnet/tree/master/examples#greeter) |
| [PingPongWithGrpcStreaming](#pingpongwithgrpcstreaming)   | Server streaming     | Code-first (hand-written)  | [Counter](https://github.com/grpc/grpc-dotnet/tree/master/examples#counter) |
| [GreeterCodeFirstGrpc](#greetercodeFirstgrpc)             | Unary + server streaming | Code-first (generated) | [Coder](https://github.com/grpc/grpc-dotnet/tree/master/examples#coder) |
| [GreeterProtoFirstGrpc](#greeterprotofirstgrpc)           | Unary + server streaming + exception mapping | Proto-first | [Greeter](https://github.com/grpc/grpc-dotnet/tree/master/examples#greeter) |
| [RacerWithGrpc](#racerwithgrpc)                           | Bidirectional streaming | Code-first (hand-written) | [Racer](https://github.com/grpc/grpc-dotnet/tree/master/examples#racer) |
| [GreeterWithGrpcErrors](#greeterwithgrpcerrors)           | Unary + rich error details | Code-first (hand-written) | (no direct equivalent — closest is Greeter + a custom interceptor) |
| [ProgressTrackerWithGrpc](#progresstrackerwithgrpc)       | Server streaming + cancellation | Code-first (generated) | [Progressor](https://github.com/grpc/grpc-dotnet/tree/master/examples#progressor) |
| [OrderChainWithGrpc](#orderchainwithgrpc)                 | Wolverine → Wolverine chain via typed client | Code-first (hand-written) | (no direct equivalent — grpc-dotnet assumes users hand-write propagation) |

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

## GreeterCodeFirstGrpc

Layout: [`src/Samples/GreeterCodeFirstGrpc/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterCodeFirstGrpc)
with three projects — `Messages`, `Server`, `Client`.

The zero-boilerplate codegen showcase. The only artifacts in the server project are handlers.
No concrete service class is written — `[WolverineGrpcService]` on the interface in `Messages`
is the only instruction Wolverine needs:

```csharp
[ServiceContract]
[WolverineGrpcService]
public interface IGreeterCodeFirstService
{
    Task<GreetReply> Greet(GreetRequest request, CallContext context = default);
    IAsyncEnumerable<GreetReply> StreamGreetings(StreamGreetingsRequest request, CallContext context = default);
}
```

At startup, `MapWolverineGrpcServices()` discovers the interface, generates
`GreeterCodeFirstServiceGrpcHandler`, and maps it. The server project's `Program.cs` is three
lines beyond a standard Wolverine host — `AddCodeFirstGrpc()`, `AddWolverineGrpc()`, and an
`IncludeAssembly` call so the scan reaches the `Messages` project:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
    opts.Discovery.IncludeAssembly(typeof(IGreeterCodeFirstService).Assembly);
});
builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();
```

**What to copy**: annotate the `[ServiceContract]` interface with `[WolverineGrpcService]` and add
`opts.Discovery.IncludeAssembly(...)` when the interface lives in a shared project. The rest is
just Wolverine handlers.

### Compared to grpc-dotnet's Coder

grpc-dotnet's **Coder** example is also code-first (protobuf-net.Grpc), but the service author
writes a concrete class that inherits from a base class and implements each method by hand. The
Wolverine sample produces the identical wire protocol with zero service class code — the generated
`GreeterCodeFirstServiceGrpcHandler` plays the role that the hand-written class plays in the
official example.

## ProgressTrackerWithGrpc

Layout: [`src/Samples/ProgressTrackerWithGrpc/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProgressTrackerWithGrpc)
with three projects — `Messages`, `Server`, `Client`.

A realistic server-streaming sample built on the zero-boilerplate codegen path. The client submits
a job description; the server streams back one `JobProgress` update per completed step. The handler
simulates work with a configurable per-step delay and yields progress as it goes:

```csharp
public static async IAsyncEnumerable<JobProgress> Handle(
    RunJobRequest request,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    for (var step = 1; step <= request.Steps; step++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(request.StepDelayMs, cancellationToken);

        var pct = (int)(step * 100.0 / request.Steps);
        yield return new JobProgress
        {
            Step = step,
            TotalSteps = request.Steps,
            PercentComplete = pct,
            Message = $"[{request.JobName}] Completed step {step}/{request.Steps}"
        };
    }
}
```

The client demonstrates both the happy path (all steps complete) and mid-stream cancellation:

```csharp
// Full job
await foreach (var p in tracker.RunJob(new RunJobRequest { JobName = "build", Steps = 10, StepDelayMs = 100 }))
    Console.WriteLine($"  [{p.PercentComplete,3}%] {p.Message}");

// Cancel mid-stream at step 3
using var cts = new CancellationTokenSource();
try
{
    await foreach (var p in tracker.RunJob(request, cts.Token))
    {
        Console.WriteLine($"  [{p.PercentComplete,3}%] {p.Message}");
        if (p.Step == 3) cts.Cancel();
    }
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
{
    Console.WriteLine("  Job cancelled by client.");
}
```

**What to copy**: `IAsyncEnumerable<T>` + `[EnumeratorCancellation]` in the handler, `await Task.Delay(..., cancellationToken)` for cooperative cancellation, and the `RpcException(Cancelled)` catch on the client side. Cancellation from the client propagates through `CallContext.CancellationToken` all the way to the handler's token without any extra wiring.

### Compared to grpc-dotnet's Progressor

grpc-dotnet's **Progressor** example writes to `IServerStreamWriter<T>` directly inside the service
override to report a countdown. The Wolverine sample keeps the service layer invisible — the handler
is a plain `IAsyncEnumerable<T>` method that knows nothing about gRPC. The progress stream and the
cancellation story are identical on the wire; only the authoring model differs.

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

## OrderChainWithGrpc

Layout: [`src/Samples/OrderChainWithGrpc/`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderChainWithGrpc)
with four projects — `Contracts`, `OrderServer` (upstream, port 5006), `InventoryServer`
(downstream, port 5007), and `OrderClient` (a plain grpc-dotnet console that kicks the chain off).

The sample's purpose is to prove two things that none of the other samples can show:

1. **Envelope-header propagation across a Wolverine-to-Wolverine hop, with zero user plumbing.**
   The upstream handler injects `IInventoryService` (registered via
   `AddWolverineGrpcClient<IInventoryService>()`) and calls it like any other collaborator — no
   `Metadata` assembly, no `CallOptions`, no custom interceptor. The downstream handler sees
   `IMessageContext.CorrelationId`, `IMessageContext.TenantId`, and `Envelope.ParentId` populated
   with the upstream values, echoes them back on the reply, and the client prints the
   round-tripped correlation-id so the preservation is visually verifiable.
2. **Typed-exception round-trip across the hop.** The downstream handler throws
   `KeyNotFoundException` when the SKU is `UNKNOWN`. The server-side exception interceptor maps
   it to `StatusCode.NotFound`; the upstream's *client-side* exception interceptor translates
   that back to `KeyNotFoundException` at the handler's call site; when the handler rethrows,
   the upstream's server-side interceptor re-maps to `NotFound` for the external caller. End-to-end
   typed-exception plumbing with zero code translating between layers.

The one registration line that makes the whole thing work (see `OrderServer/Program.cs`):

<!-- snippet: sample_order_chain_add_wolverine_grpc_client -->
<a id='snippet-sample_order_chain_add_wolverine_grpc_client'></a>
```cs
// The one new registration line compared to a normal Wolverine gRPC server. Wolverine resolves
// IInventoryService into any handler (like PlaceOrderHandler) that asks for it, routes the call
// through the typed gRPC client, and stamps envelope headers automatically. No GrpcChannel, no
// Metadata wiring, no custom interceptors.
builder.Services.AddWolverineGrpcClient<IInventoryService>(o =>
{
    o.Address = new Uri("http://localhost:5007");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderChainWithGrpc/OrderServer/Program.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_chain_add_wolverine_grpc_client' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

**What to copy**: `AddWolverineGrpcClient<T>()` in the calling service + an ordinary Wolverine
handler that takes the typed client as a constructor parameter. The rest — envelope propagation,
exception mapping, channel management — Wolverine and `Grpc.Net.ClientFactory` handle for you.

### Running the sample

```bash
# Three terminals, bottom-up:
dotnet run --project src/Samples/OrderChainWithGrpc/InventoryServer
dotnet run --project src/Samples/OrderChainWithGrpc/OrderServer
dotnet run --project src/Samples/OrderChainWithGrpc/OrderClient
```

The client's success path prints the reservation plus the correlation-id seen on both hops; the
failure path prints the `NotFound` surfaced by the upstream server.

### Compared to grpc-dotnet examples

There's no direct equivalent in the grpc-dotnet repo — the official examples assume users
hand-write client interceptors for anything they want stamped on outgoing calls, and none of the
shipped examples chain one service's handler into another service's RPC. The Wolverine sample
collapses that entire surface area into a single registration line, because ambient context and
typed-exception symmetry are *the* things the `AddWolverineGrpcClient<T>()` extension exists to
provide.

## Related

- [Index](./) — overview + getting started.
- [How gRPC Handlers Work](./handlers) — why the samples can keep their service classes tiny.
- [Code-First and Proto-First Contracts](./contracts) — pick the style each sample uses.
- [Streaming](./streaming) — the streaming shapes two of these samples implement.
- [Error Handling](./errors) — the pipeline `GreeterWithGrpcErrors` exercises end-to-end.
- [Typed gRPC Clients](./client) — full reference for the `AddWolverineGrpcClient<T>()` extension
  `OrderChainWithGrpc` demonstrates.
