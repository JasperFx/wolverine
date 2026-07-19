# Multi-Tenancy and gRPC

::: tip
For a holistic overview of multi-tenancy across all of Wolverine, see the [Multi-Tenancy Tutorial](/tutorials/multi-tenancy).
The equivalent feature for HTTP endpoints is described in [Multi-Tenancy and ASP.Net Core](/guide/http/multi-tenancy).
:::

::: info
Server-side tenant id detection for Wolverine.Grpc was added for
[GH-3368](https://github.com/JasperFx/wolverine/issues/3368). It mirrors the
`ITenantDetectionPolicies` API that Wolverine.HTTP has had since 1.7.0.
:::

The first part of any multi-tenancy approach in gRPC services is detecting which tenant should be
active for the current call. Wolverine.Grpc calls this "tenant id detection," and it works the same
way as [Wolverine.HTTP's version](/guide/http/multi-tenancy): you configure one or more detection
strategies, Wolverine weaves a detection step into the *generated* service wrapper for every
Wolverine-managed gRPC service (proto-first, code-first, and hand-written delegation wrappers
alike), and the detected tenant id:

1. Is assigned to the scoped `IMessageBus.TenantId` **before** the RPC forwards to
   `InvokeAsync`/`StreamAsync`, so the envelope — and any Marten/Polecat tenant-scoped session the
   handler uses — carries the right tenant.
2. Declares the same `tenantId` code-generation variable that Marten's and Polecat's
   session-opening frames look for, so persistence middleware woven into the gRPC chain itself
   gets the tenant structurally, without relying on the ambient `IMessageContext`.

## Tenant Id Detection

Configure detection on `WolverineGrpcOptions.TenantId` inside `AddWolverineGrpc()`:

```csharp
builder.Services.AddWolverineGrpc(grpc =>
{
    // The tenant detection is fall through, so the first strategy
    // that finds anything wins!

    // Use the value of a named request metadata header
    // (matched case-insensitively)
    grpc.TenantId.IsRequestHeaderValue("tenant-id");

    // Detect the tenant id from an expected claim on the
    // authenticated ClaimsPrincipal of the current call
    grpc.TenantId.IsClaimTypeNamed("tenant-id");

    // If the tenant id cannot be detected otherwise, fall back
    // to a designated tenant id
    grpc.TenantId.DefaultIs("default_tenant");
});
```

The built-in strategies are:

| Strategy                          | Reads from                                                                                    |
|-----------------------------------|-----------------------------------------------------------------------------------------------|
| `IsRequestHeaderValue(headerKey)` | Inbound request metadata (`ServerCallContext.RequestHeaders`), case-insensitive               |
| `IsClaimTypeNamed(claimType)`     | The authenticated `ClaimsPrincipal` (`context.GetHttpContext().User`, where grpc-aspnetcore surfaces authentication results) |
| `DefaultIs(tenantId)`             | Nothing — always returns the fallback value. Register it last.                                |

There are no gRPC equivalents of HTTP's route-argument, query-string, or sub-domain strategies —
those concepts don't exist on a gRPC call. Metadata headers and claims are the idiomatic carriers,
and anything else can be plugged in with a custom strategy.

## Zero-Config Default

If you never configure `TenantId` at all and `WolverineGrpcOptions.PropagateEnvelopeHeaders`
is `true` (its default), Wolverine automatically detects the tenant from the `tenant-id` request
metadata header — the exact header that [`AddWolverineGrpcClient<T>()`](./client)'s propagation
interceptor stamps on outgoing calls from `IMessageContext.TenantId`. In other words, a
Wolverine-to-Wolverine gRPC hop round-trips the tenant id **with zero server configuration**:

```
caller handler (TenantId = "tenant1")
  → Wolverine gRPC client stamps 'tenant-id: tenant1' metadata
    → generated server wrapper detects it and sets bus.TenantId = "tenant1"
      → server handler + tenant-scoped sessions run as "tenant1"
```

Explicitly configuring any `TenantId` strategy replaces the zero-config default (register
`grpc.TenantId.IsRequestHeaderValue("tenant-id")` yourself if you want it *in addition to* your
own strategies), and setting `PropagateEnvelopeHeaders = false` suppresses it entirely.

Note that the zero-config default overlaps with the runtime
[`WolverineGrpcServicePropagationInterceptor`](./client#envelope-header-propagation), which also
copies the `tenant-id` header onto the scoped `IMessageContext`. The two are complementary: the
interceptor covers *every* mapped gRPC service (including services Wolverine did not generate)
at runtime, while the detection frame makes the tenant a structural part of the generated code —
visible in `codegen preview`, available to persistence frames as the `tenantId` variable, and
independent of the ambient message context.

## Custom Tenant Detection

For anything beyond headers and claims, implement `IGrpcTenantDetection`:

```csharp
public class MyCustomDetection : IGrpcTenantDetection
{
    // Return the tenant id, or null for "not found" so the
    // next strategy gets a chance
    public ValueTask<string?> DetectTenant(ServerCallContext context)
    {
        var value = context.RequestHeaders
            .FirstOrDefault(e => e.Key == "x-account-key")?.Value;

        return new ValueTask<string?>(value is null ? null : Lookup(value));
    }
}
```

and register it either as an instance or by type:

```csharp
builder.Services.AddWolverineGrpc(grpc =>
{
    // Direct instance
    grpc.TenantId.DetectWith(new MyCustomDetection());

    // Or by type — the instance is built from your application's
    // IoC container during bootstrapping, with singleton scoping
    grpc.TenantId.DetectWith<MyCustomDetection>();
});
```

## Requiring a Tenant Id

To make the tenant id mandatory, add `AssertExists()`:

```csharp
builder.Services.AddWolverineGrpc(grpc =>
{
    grpc.TenantId.IsRequestHeaderValue("tenant-id");
    grpc.TenantId.AssertExists();
});
```

When no strategy detects a tenant id, the generated service throws an `RpcException` with status
`InvalidArgument` and the detail message
`"No mandatory tenant id could be detected for this gRPC call"` before the call ever reaches a
Wolverine handler. `InvalidArgument` is the [canonical gRPC mapping](https://google.aip.dev/193)
of the **400 Bad Request** that Wolverine.HTTP's `AssertExists()` returns for the same condition —
the caller sent a structurally incomplete request. If your security model treats a missing tenant
as an authentication failure instead, skip `AssertExists()` and enforce the claim through ASP.NET
Core authorization, which will surface as `Unauthenticated`/`PermissionDenied`.

## Scope and Limitations

- Detection is woven into Wolverine-*generated* service wrappers only: proto-first stubs,
  code-first `[WolverineGrpcService]` contracts, and hand-written service classes that receive a
  generated delegation wrapper. Services you map directly with `MapGrpcService<T>()` (without a
  Wolverine wrapper) are still covered by the runtime propagation interceptor for the `tenant-id`
  header, but do not run custom detection strategies — read the header yourself there.
- For **code-first and hand-written** services, detection requires the RPC method to declare a
  `CallContext` parameter — that's the only route to the underlying `ServerCallContext` and its
  request metadata. Methods without one fall back to the runtime interceptor.
- Code-first **server-streaming** methods (returning `IAsyncEnumerable<T>` directly) can't host
  the async detection step. Code-first **unary and client-streaming** methods return
  `Task<TResponse>`, so detection is woven whenever they declare a `CallContext` parameter.
  Proto-first server-streaming, client-streaming, and bidirectional methods are fully covered —
  every proto-first RPC shape ends with a `ServerCallContext` parameter, which is all detection
  needs.
