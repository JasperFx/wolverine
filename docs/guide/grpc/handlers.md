# How gRPC Handlers Work

Wolverine's gRPC adapter is deliberately thin: the gRPC service class is a small **shim** that
forwards to `IMessageBus.InvokeAsync` or `IMessageBus.StreamAsync`, and the handler on the other side
is an ordinary Wolverine handler. Everything Wolverine already does for messages — handler discovery,
middleware, codegen, tracing, error mapping — applies unchanged.

This page explains the shape of that flow, and how a gRPC handler compares to the HTTP and messaging
handlers you already know.

## The service → bus → handler flow

A gRPC RPC takes the following path through Wolverine:

```
┌──────────────┐   RPC     ┌────────────────────┐   InvokeAsync<T>   ┌──────────────┐
│  gRPC client │ ───────►  │ gRPC service shim  │ ─────────────────► │ Wolverine    │
└──────────────┘           │  (code-first or    │                    │  handler     │
                           │   generated proto- │                    │  (Handle /   │
                           │   first wrapper)   │  IAsyncEnumerable  │   Consume)   │
                           └────────────────────┘ ◄───────────────── └──────────────┘
                                    ▲  StreamAsync<T>
                                    │
                           WolverineGrpcExceptionInterceptor
                           (+ optional rich-details providers)
```

Three things are worth calling out:

1. The **service shim** is trivial. In code-first it's a one-line method; in proto-first Wolverine
   generates the wrapper for you at startup. You don't write business logic there.
2. The **handler** never references gRPC. It takes a request DTO and returns a response (or an
   `IAsyncEnumerable<T>`). The same handler can back an HTTP endpoint or an async message.
3. **Exceptions travel through an interceptor**, not inside the handler. Throw ordinary .NET
   exceptions and the interceptor turns them into `RpcException` with the right status code — see
   [Error Handling](./errors).

## How gRPC handlers differ from HTTP and messaging handlers

All three handler styles share the same core: Wolverine discovers `Handle` / `Consume` methods and
generates an optimised dispatch chain. The differences are in the **edge layer** and the **shape of
the response**:

| Aspect | Messaging handler | HTTP endpoint | gRPC handler |
|--------|-------------------|---------------|--------------|
| **Invoked by** | Transport listener (Rabbit, SQS, etc.) or `IMessageBus.InvokeAsync` | ASP.NET Core routing | gRPC service method forwarding to `IMessageBus` |
| **Primary entry point** | `Handle(T message, …)` on a handler class | Route method on an endpoint class (`[WolverineGet]` etc.) | `Handle(T request, …)` — same shape as messaging |
| **Discovered as** | Any `Handle`/`Consume` method that takes a non-framework type | Annotated endpoint method | Same as messaging — the gRPC adapter doesn't introduce a separate handler category |
| **Transport concerns** | Retries, durable outbox, DLQ | HTTP verbs, content negotiation, `ProblemDetails` | `RpcException`, `grpc-status-details-bin`, HTTP/2 framing |
| **Streaming** | Not directly — use sagas or outbound messages | Not native — server-sent events or chunked responses | First-class: handler returns `IAsyncEnumerable<T>` |
| **Cancellation** | Token from the listener's shutdown signal | `HttpContext.RequestAborted` | Client's `CallContext.CancellationToken` flows in |
| **Error conversion** | Retry/DLQ policies | `ProblemDetails` / `ValidationProblemDetails` | AIP-193 `StatusCode` table + optional `google.rpc.Status` details |

The mental model: **a gRPC handler is a messaging handler with a richer edge contract.** The handler
itself doesn't know which of the three invoked it.

### Why that matters in practice

- **You don't need a separate "gRPC handler" type.** If a plain `public static class FooHandler`
  already handles `FooRequest` over the bus, the gRPC service shim can forward to it without you
  writing anything new.
- **Middleware applies identically.** `UseFluentValidation()`, open/generic middleware, saga
  middleware, etc., all run on the gRPC path because Wolverine is invoking the handler through the
  same pipeline it always does.
- **The transport concerns stay at the edge.** You won't see `RpcException` inside a handler, and
  you shouldn't throw one there either — throw the domain exception and let the interceptor
  translate it.

## Discovery and codegen

`MapWolverineGrpcServices()` does two passes when the host starts:

1. **Code-first**: any class whose name ends in `GrpcService` (or that carries
   `[WolverineGrpcService]`) is picked up and mapped via protobuf-net.Grpc's routing.
2. **Proto-first**: any abstract class carrying `[WolverineGrpcService]` and subclassing a
   generated `{Service}Base` triggers codegen. Wolverine emits a concrete
   `{ProtoServiceName}GrpcHandler` that overrides each RPC and forwards to `IMessageBus`.

Both paths feed into the same generated-code pipeline used by Wolverine's messaging and HTTP
adapters, so your gRPC services show up in the standard diagnostics:

```bash
dotnet run -- describe
dotnet run -- describe-routing
```

If you're debugging discovery, those commands will show the generated handler type name and the
handler method it forwards to.

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
