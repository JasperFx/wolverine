# Typed gRPC Clients

`AddWolverineGrpcClient<T>()` is a thin Wolverine-flavored wrapper over the Microsoft gRPC client factory
(`Grpc.Net.ClientFactory.AddGrpcClient<T>()`). It layers three conveniences onto the standard path without
replacing it:

1. **Envelope-header propagation** — `correlation-id`, `tenant-id`, `parent-id`, `conversation-id`, and
   `message-id` are stamped on outgoing calls whenever an `IMessageContext` is resolvable from the current
   DI scope. The wire vocabulary is the same [`EnvelopeConstants`](https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/EnvelopeConstants.cs)
   every other Wolverine transport uses, so a call routed through the gRPC client preserves the same
   correlation identity a message-bus hop would.
2. **`RpcException` → typed-exception translation** — the client-side inverse of
   [`WolverineGrpcExceptionInterceptor`](./errors). An inbound `RpcException` with `StatusCode.NotFound`
   surfaces as `KeyNotFoundException`, `InvalidArgument` as `ArgumentException`, and so on — with the
   original `RpcException` preserved on `InnerException` so rich error details (trailers, status-detail
   payloads) are never lost.
3. **Uniform registration for both contract styles** — a single entry point handles
   `protobuf-net.Grpc` code-first `[ServiceContract]` interfaces *and* proto-first `Grpc.Tools`-generated
   concrete clients. Wolverine detects the style automatically and routes each through the correct
   substrate.

::: tip Still-supported alternative
Raw `GrpcChannel` + generated stubs — the pattern used by the samples in this section — remains a
first-class path. `AddWolverineGrpcClient<T>()` is adoption-driven sugar, not a replacement. If a
`GrpcChannel` in `Program.cs` works for your project, you can keep it.
:::

## Registration

Call `AddWolverineGrpcClient<T>()` against an `IServiceCollection` and set, at a minimum, the
`Address`. The extension returns a `WolverineGrpcClientBuilder` that exposes further configuration
without revealing which substrate was chosen:

```csharp
builder.Services.AddWolverineGrpcClient<IPingService>(o =>
{
    o.Address = new Uri("https://ponger.example");
});
```

From there, inject the typed client into any Wolverine handler, minimal-API endpoint, or background
service the same way you would a generated client:

```csharp
public static async Task<PongReply> Handle(PingRequest request, IPingService ping, CancellationToken ct)
{
    return await ping.Ping(request);
}
```

### Code-first vs proto-first

`IsCodeFirstContract` classifies `TClient` by whether it is an interface decorated with
`[ServiceContract]` (the `protobuf-net.Grpc` convention). The two cases route differently:

| Kind         | Detected when `TClient` is…                                | Substrate                                                         |
|--------------|------------------------------------------------------------|-------------------------------------------------------------------|
| `CodeFirst`  | an interface with `[ServiceContract]`                      | Wolverine's own channel factory (`WolverineGrpcCodeFirstChannelFactory`) |
| `ProtoFirst` | a concrete class (e.g. the generated `Greeter.GreeterClient`) | `Grpc.Net.ClientFactory.AddGrpcClient<T>()` (Microsoft's factory)    |

Proto-first registrations also expose the underlying `IHttpClientBuilder` via
`builder.HttpClientBuilder` so you can wire Polly, `IHttpMessageHandlerBuilderFilter`, or any other
`IHttpClientFactory` extension point. Code-first registrations do not ride on `IHttpClientFactory`,
so `builder.HttpClientBuilder` is `null` — use `ConfigureChannel` instead.

### The address is required

`WolverineGrpcClientOptions.Address` is intentionally nullable at the type level so the builder can
compose across multiple `Configure(...)` calls. Resolution-time validation throws a clear
`InvalidOperationException` if it was never set by the time a client is pulled out of the container,
naming the contract type. This mirrors the server-side AIP-193 mapping philosophy — loud, early
errors over silent misconfiguration.

## Envelope-header propagation

`WolverineGrpcClientPropagationInterceptor` runs on every call shape (unary, server-streaming,
client-streaming, duplex-streaming, blocking unary). On each call it resolves `IMessageContext` from
the current DI scope and stamps the five envelope identifiers onto the outgoing call's `Metadata`:

| Header             | Source                                     |
|--------------------|--------------------------------------------|
| `correlation-id`   | `IMessageContext.CorrelationId`            |
| `tenant-id`        | `IMessageContext.TenantId`                 |
| `message-id`       | `IMessageContext.Envelope.Id`              |
| `parent-id`        | `IMessageContext.Envelope.ParentId`        |
| `conversation-id`  | `IMessageContext.Envelope.ConversationId`  |

The design notes worth knowing:

- The interceptor **never overwrites** a header the caller stamped themselves. Per-call
  `Metadata` passed through `CallOptions` wins. This keeps explicit overrides (e.g. impersonating
  a specific tenant for a background job) idiomatic.
- If there is **no `IMessageContext` in scope** (a bare `Program.cs` caller, a test harness without
  the Wolverine bus, etc.) the interceptor silently no-ops. The call still goes through — just
  without Wolverine-specific headers.
- Propagation can be **disabled per client** by setting
  `WolverineGrpcClientOptions.PropagateEnvelopeHeaders = false`. Rarely needed, but occasionally
  useful when the server is a third-party service that does not understand Wolverine's metadata
  vocabulary.

```csharp
builder.Services.AddWolverineGrpcClient<IPingService>(o =>
{
    o.Address = new Uri("https://ponger.example");
    o.PropagateEnvelopeHeaders = false; // opt out
});
```

On the server side of a Wolverine→Wolverine hop, the envelope headers are read back in the
`WolverineGrpcServicePropagationInterceptor` already shipped with the adapter, so a call chain
spanning multiple Wolverine services keeps a single correlation identity without any user wiring.

## `RpcException` → typed-exception translation

`WolverineGrpcClientExceptionInterceptor` catches `RpcException` before it surfaces to your handler
code and substitutes a typed .NET exception using the inverse of the server-side AIP-193 table:

| gRPC Status Code                           | .NET Exception                       |
|--------------------------------------------|--------------------------------------|
| `Cancelled`                                | `OperationCanceledException`         |
| `DeadlineExceeded`                         | `TimeoutException`                   |
| `InvalidArgument`                          | `ArgumentException`                  |
| `NotFound`                                 | `KeyNotFoundException`               |
| `PermissionDenied`, `Unauthenticated`      | `UnauthorizedAccessException`        |
| `FailedPrecondition`                       | `InvalidOperationException`          |
| `Unimplemented`                            | `NotImplementedException`            |
| *anything else* (`Internal`, `Unknown`, …) | *original `RpcException`, unchanged* |

The original `RpcException` is always preserved on `InnerException` so `grpc-status-details-bin`
trailers, `Status.Detail`, and the full gRPC diagnostic surface remain reachable:

```csharp
try
{
    var reply = await client.GetOrder(new GetOrderRequest { Id = 42 });
}
catch (KeyNotFoundException ex)
{
    // ex.Message   → Status.Detail from the server
    // ex.InnerException is RpcException — inspect trailers / rich details here
    var rpc = (RpcException)ex.InnerException!;
}
```

Streaming responses are translated per `MoveNextAsync`: an `RpcException` raised after the first
yielded item surfaces as the typed exception from inside the `await foreach` loop, not from the
outer `client.StreamCall(...)` invocation.

### Per-client override

Some integrations need bespoke mapping — translating a specific `StatusCode` to a domain-specific
exception, or mapping trailers onto a richer exception type. Supply a
`MapRpcException` callback on the options:

```csharp
builder.Services.AddWolverineGrpcClient<ITenantService>(o =>
{
    o.Address = new Uri("https://tenant.example");
    o.MapRpcException = ex => ex.StatusCode == StatusCode.NotFound
        ? new TenantNotFoundException(ex.Status.Detail, ex)
        : null;  // null → fall through to the default table
});
```

The override is consulted first; returning `null` forwards to the default mapping so you only need
to cover the status codes you care about.

## Escape hatches

### `ConfigureChannel`

For any knob exposed by `GrpcChannelOptions` but not by `WolverineGrpcClientOptions`:

```csharp
builder.Services
    .AddWolverineGrpcClient<IPingService>(o => o.Address = new Uri("https://ponger.example"))
    .ConfigureChannel(channel =>
    {
        channel.MaxReceiveMessageSize = 16 * 1024 * 1024;
        channel.Credentials = ChannelCredentials.SecureSsl;
    });
```

`ConfigureChannel` works across both code-first and proto-first registrations — for proto-first it
is applied via the factory's `ChannelOptionsActions`; for code-first it is applied when Wolverine
materializes the channel inside `WolverineGrpcCodeFirstChannelFactory`.

### `HttpClientBuilder` (proto-first only)

If you need `IHttpClientFactory` extension points directly — Polly resilience, primary handler
replacement, per-environment message handlers — `builder.HttpClientBuilder` is non-null on the
proto-first path:

```csharp
builder.Services
    .AddWolverineGrpcClient<Greeter.GreeterClient>(o => o.Address = new Uri("https://greeter.example"))
    .HttpClientBuilder!
    .AddStandardResilienceHandler();
```

The Wolverine exception interceptor is registered *outermost* in the pipeline on purpose: when you
add `AddStandardResilienceHandler` (or other Polly-based handlers), retries run *inside* the
exception catch, so the final exception surfaced to your code still reflects the final outcome
after retries — not the first transient failure translated into `TimeoutException`.

## Ordering and composition

The interceptor stack is constructed so that:

1. **Exception translation** is the outermost concern. Retries and other Polly policies live
   underneath, and their final outcome is what the typed-exception mapper sees.
2. **Propagation** sits inside the exception interceptor. A retry that Polly issues gets a fresh
   stamp of the current `IMessageContext` — not stale headers captured before the retry.

If you add your own interceptor via `builder.HttpClientBuilder!.AddInterceptor(...)` (proto-first)
it lands inside both Wolverine interceptors, which is what you almost always want.

## API Reference

| Type / Member                                         | Purpose                                                                                |
|-------------------------------------------------------|----------------------------------------------------------------------------------------|
| `AddWolverineGrpcClient<TClient>()`                   | Registers a typed gRPC client with Wolverine propagation + exception translation.      |
| `WolverineGrpcClientOptions`                          | Named options for a registered client — `Address`, `PropagateEnvelopeHeaders`, `MapRpcException`. |
| `WolverineGrpcClientBuilder`                          | Return value: `Kind`, `HttpClientBuilder` (proto-first only), `ConfigureChannel(...)`. |
| `WolverineGrpcClientKind`                             | `CodeFirst` / `ProtoFirst` — exposed on the builder for discovery.                     |
| `WolverineGrpcClientPropagationInterceptor`           | Stamps envelope headers on each call.                                                  |
| `WolverineGrpcClientExceptionInterceptor`             | Translates `RpcException` to typed .NET exceptions per `MapRpcException` + the default table. |
| `WolverineGrpcExceptionMapper.MapToException(rpc)`    | Public default mapping table; use from custom interceptors if needed.                  |

## See also

- [Error Handling](./errors) — the server-side mapping the client-side `MapToException` table mirrors.
- [How gRPC Handlers Work](./handlers) — the server-side propagation interceptor that reads back
  the headers stamped here.
