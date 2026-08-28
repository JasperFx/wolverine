# Idempotency Keys

<Badge type="tip" text="6.31" />

::: tip
This is the gRPC half of Wolverine's [logical message
deduplication](/guide/durability/idempotency#logical-message-deduplication). Read that page first
for the storage, the retention window, and why this is opt-in.
:::

A unary RPC that creates something has the same at-most-once problem as an HTTP `POST`: the client
retries after a deadline it cannot distinguish from a failure, an operator triggers the call twice,
a queued call replays when connectivity returns.

Wolverine reads the logical id from the call's **request metadata**, under the conventional
`idempotency-key` key.

## Deduplicating an RPC

`[Deduplicated]` goes on the individual RPC method, on any of the three service flavours — proto-first
stubs, code-first contracts, and hand-written service classes:

```csharp
[ServiceContract]
[WolverineGrpcService]
public interface IOrderService
{
    [Deduplicated]
    Task<OrderReply> CreateOrder(CreateOrder request, CallContext context = default);

    // Untouched — the requirement is resolved per RPC, not per service
    Task<OrderReply> GetOrder(GetOrder request, CallContext context = default);
}
```

Putting it on the service type instead applies it to every RPC on that service; a method-level
attribute always wins over the type-level one.

With `Durability.EnableMessageDeduplication` turned on, `CreateOrder` now:

- runs normally for the first call carrying a given `idempotency-key`
- throws `RpcException` with **`StatusCode.AlreadyExists`** for any later call carrying the same key
  within the [deduplication window](/guide/durability/idempotency#the-deduplication-window)
- throws `RpcException` with **`StatusCode.InvalidArgument`** for a call carrying no key at all

Both codes come from [AIP-193](https://google.aip.dev/193) — the same table
[`WolverineGrpcExceptionInterceptor`](/guide/grpc/errors) already maps ordinary .NET exceptions
through, so a client sees deduplication refusals in the shape it already handles every other refusal
in.

Throwing rather than returning is not a stylistic choice. A unary RPC has to produce a response
message, and there is no honest response body for "this was already done" — the status is the answer.

## Sending the key

```csharp
var headers = new Metadata { { "idempotency-key", $"{scheduleId}|{occurrence:O}" } };
var reply = await client.CreateOrder(request, new CallContext(new CallOptions(headers)));
```

::: warning
gRPC metadata keys are lower-case. `Grpc.Core.Metadata` rejects a key containing upper-case
characters outright, so `Idempotency-Key` will not compile into a working call. Wolverine lower-cases
the configured header name on the server side for exactly this reason, so `[Deduplicated]` and
`[Deduplicated("Idempotency-Key")]` both match a client sending `idempotency-key`.
:::

## Which RPC shapes are supported

The logical id lives in request metadata, which every call shape carries — but the generated method
has to be able to *await* the claim before doing any work, and it needs a route to the
`ServerCallContext`.

| Flavour | Supported shapes |
|---|---|
| Proto-first | all four (unary, server-streaming, client-streaming, bidirectional) |
| Code-first | unary and client-streaming, with a `CallContext` parameter |
| Hand-written | unary, with a `CallContext` parameter |

The code-first and hand-written limits are the same ones [tenant
detection](/guide/grpc/multi-tenancy) already has: a server-streaming method returns
`IAsyncEnumerable<T>` directly, so its body cannot await, and without a `CallContext` parameter there
is no route to the request metadata at all.

Asking for deduplication on a shape that cannot support it is a **bootstrap failure** naming the
method, not a silently unprotected RPC. Add a `CallContext` parameter, or drop the attribute.

## Other key sources

Unlike message handlers and HTTP endpoints, gRPC services can only source the id from request
metadata. `ValueSource.InputMember` and friends are rejected at bootstrap: the generated wrapper
forwards the request to the message bus rather than binding its members, so there is no place to
read one from.

## Failed calls do not poison the key

A gRPC service method is never enrolled in a Wolverine transaction of its own — it forwards to the
bus, and any transaction belongs to the handler on the other side. So the claim is always already
committed by the time the forward runs, and Wolverine always issues a compensating release when the
call throws. A retry with the same key gets through.
