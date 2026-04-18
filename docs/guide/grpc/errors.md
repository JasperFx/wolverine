# Error Handling

Wolverine's gRPC adapter has two layers of error conversion:

1. A **default mapping table** (always on) that translates common .NET exceptions to the matching
   gRPC `StatusCode`, following [Google AIP-193](https://google.aip.dev/193). This is the gRPC
   counterpart to Wolverine's default HTTP error behaviour.
2. An **opt-in `google.rpc.Status` pipeline** for structured, field-level detail payloads — the
   gRPC counterpart to HTTP's [`ProblemDetails`](../http/problemdetails) /
   `ValidationProblemDetails`.

Both layers coexist: the rich-details pipeline runs first, and anything it doesn't handle falls
through to the default table.

## Default mapping (AIP-193)

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

A handler can just throw `KeyNotFoundException` and the gRPC client receives an `RpcException` with
`StatusCode.NotFound` — no explicit translation layer in every service method:

```csharp
public static OrderReply Handle(GetOrder request, IOrderStore store)
{
    var order = store.Find(request.OrderId)
        ?? throw new KeyNotFoundException($"Order {request.OrderId}");  // → NotFound
    return new OrderReply { /* ... */ };
}
```

Throwing `RpcException` directly remains the escape hatch for status codes or trailers not in
either table.

## Rich error details (opt-in)

The default mapping table produces a single `StatusCode` and a message string — enough for
"something went wrong," but not for a client that needs to render per-field validation errors or
inspect a machine-readable reason code. For that, Wolverine ships an opt-in
[AIP-193 `google.rpc.Status`](https://google.aip.dev/193) pipeline that packs structured detail
payloads into the `grpc-status-details-bin` trailer.

This is the **gRPC counterpart to** [`ProblemDetails`](../http/problemdetails) on the HTTP side.
The same handler can surface a `ValidationProblemDetails` response over HTTP and a
`google.rpc.BadRequest` payload over gRPC — both driven by the same Wolverine middleware and the
same handler code.

### Turning it on

Two extension methods wire the pipeline:

```csharp
builder.Host.UseWolverine(opts =>
{
    // Exceptions thrown by handlers bubble into the gRPC interceptor.
    opts.UseFluentValidation();

    // Opt in to google.rpc.Status + grpc-status-details-bin. Idempotent.
    opts.UseGrpcRichErrorDetails();

    // Bridge package: translates FluentValidation.ValidationException →
    // google.rpc.BadRequest with one FieldViolation per failure.
    opts.UseFluentValidationGrpcErrorDetails();
});
```

`UseGrpcRichErrorDetails()` is safe to call on its own — with no adapters registered the validation
provider is a no-op and the interceptor falls through to the canonical table.
`UseFluentValidationGrpcErrorDetails()` ships in a separate package
(`WolverineFx.FluentValidation.Grpc`) so hosts that don't use FluentValidation never pull the
dependency.

### Validation failures → `BadRequest`

With the pipeline on, a handler that throws `FluentValidation.ValidationException` (typically via
`UseFluentValidation()`'s middleware) surfaces on the client as:

- `RpcException.StatusCode` = `InvalidArgument`
- `grpc-status-details-bin` trailer containing
  `google.rpc.Status { Code = 3, Details = [ BadRequest { FieldViolations = [...] } ] }`

One `FieldViolation` per failure, with `Field` from the validator's property name and `Description`
from the failure message. The mapping is identical in spirit to HTTP's `ValidationProblemDetails`.

### Domain exceptions → custom details

For your own domain exceptions, use `MapException<TException>(...)` on the configuration builder:

```csharp
opts.UseGrpcRichErrorDetails(cfg =>
{
    cfg.MapException<GreetingForbiddenException>(
        StatusCode.FailedPrecondition,
        (ex, _) => new[]
        {
            new PreconditionFailure
            {
                Violations =
                {
                    new PreconditionFailure.Types.Violation
                    {
                        Type = "policy.banned_name",
                        Subject = ex.Subject,
                        Description = ex.Reason
                    }
                }
            }
        });
});
```

The factory runs per-request and gets the live `ServerCallContext`, so it can read headers, peer
info, or anything else it needs. First match wins — add multiple `MapException` entries in the
order most specific → least specific.

### Custom providers

For providers with scoped dependencies (repositories, tenant resolvers, etc.), implement
`IGrpcStatusDetailsProvider` and register via `AddProvider<T>()`:

```csharp
public sealed class CompliancePolicyProvider : IGrpcStatusDetailsProvider
{
    private readonly ITenantPolicyLookup _policies;
    public CompliancePolicyProvider(ITenantPolicyLookup policies) => _policies = policies;

    public Status? BuildStatus(Exception exception, ServerCallContext context)
    {
        if (exception is not CompliancePolicyViolation violation) return null;
        var policy = _policies.For(context);
        return new Status
        {
            Code = (int)StatusCode.PermissionDenied,
            Message = "Blocked by compliance policy",
            Details = { Any.Pack(new ErrorInfo { Reason = violation.Rule, Domain = policy.Domain }) }
        };
    }
}

opts.UseGrpcRichErrorDetails(cfg => cfg.AddProvider<CompliancePolicyProvider>());
```

The provider is resolved from the request-scoped service provider, so constructor-injected
dependencies follow normal ASP.NET Core lifetimes. Return `null` from `BuildStatus` to skip — the
next provider in the chain gets a shot.

### Opt-in catch-all `ErrorInfo`

For "everything that isn't explicitly mapped should still carry a machine-readable reason," enable
`DefaultErrorInfoProvider` as the last provider in the chain:

```csharp
opts.UseGrpcRichErrorDetails(cfg => cfg.EnableDefaultErrorInfo());
```

Unmapped exceptions become `Code.Internal` with a single
`ErrorInfo { Reason = exception.GetType().Name, Domain = "wolverine.grpc" }`. No stack traces, no
exception messages — the payload is deliberately opaque so you can turn it on in production without
leaking internals.

### Reading rich details on the client

Rich details live inside `RpcException`'s trailers. The `Grpc.StatusProto` package's
`GetRpcStatus()` extension pulls the `google.rpc.Status`, then `Any.Unpack<T>()` surfaces each
detail message:

```csharp
catch (RpcException ex)
{
    var richStatus = ex.GetRpcStatus();
    if (richStatus is null) { /* default mapping, no rich details attached */ return; }

    foreach (var detail in richStatus.Details)
    {
        if (detail.Is(BadRequest.Descriptor))
        {
            var badRequest = detail.Unpack<BadRequest>();
            foreach (var v in badRequest.FieldViolations)
                Console.WriteLine($"{v.Field}: {v.Description}");
        }
        else if (detail.Is(PreconditionFailure.Descriptor))
        {
            var precondition = detail.Unpack<PreconditionFailure>();
            // ...
        }
    }
}
```

The [GreeterWithGrpcErrors](https://github.com/JasperFx/wolverine/tree/main/src/Samples/GreeterWithGrpcErrors)
sample demonstrates both paths end-to-end.

::: warning
The `grpc-status-details-bin` trailer shares gRPC's **~8 KB header budget** with the rest of the
response metadata. Packing dozens of detail payloads (or a single payload with a large free-text
message) can exceed the limit and truncate the trailer mid-frame — the client then sees the status
code but no details. Keep payloads small: one detail message per status, short reason codes, and
refer the client to a separate RPC for deep diagnostics when it needs more.
:::
