# Streaming

gRPC has four call shapes: unary, server streaming, client streaming, and bidirectional streaming.
Wolverine currently covers **unary** and **server streaming** out of the box, with a working pattern
for **bidirectional** via a per-item bridge. **Pure client streaming** doesn't have a clean adapter
yet and will fail fast at startup in proto-first mode.

This page covers the shapes that work today, the one that doesn't, and how to cancel cleanly.

## Server streaming (first-class)

Server streaming is the natural fit for Wolverine: handlers return `IAsyncEnumerable<T>` and
Wolverine's [`IMessageBus.StreamAsync<T>`](/guide/messaging/message-bus.html#streaming-responses)
feeds the stream through the gRPC transport without buffering.

### Service shim

```csharp
// Code-first
public IAsyncEnumerable<PongReply> PingStream(PingStreamRequest request, CallContext context = default)
    => Bus.StreamAsync<PongReply>(request, context.CancellationToken);
```

```csharp
// Proto-first — Wolverine generates this wrapper for you; shown for illustration.
public override async Task StreamGreetings(StreamGreetingsRequest request,
    IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
{
    await foreach (var reply in _bus.StreamAsync<HelloReply>(request, context.CancellationToken))
    {
        await responseStream.WriteAsync(reply, context.CancellationToken);
    }
}
```

### Handler

The handler is identical for both contract styles — it's just a Wolverine handler that returns
`IAsyncEnumerable<T>` with an `[EnumeratorCancellation]` token:

```csharp
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
```

Each `yield return` corresponds to a gRPC message frame on the wire. Back-pressure happens at the
`WriteAsync` layer — if the client stops reading, `WriteAsync` will suspend, which in turn blocks
the handler's `MoveNextAsync`, which back-propagates through your `yield return`.

## Bidirectional streaming (manual bridge)

There is no `IMessageBus.StreamAsync<TRequest, TResponse>` overload today, but you can compose
bidi on top of server streaming by reading one request item at a time and calling
`Bus.StreamAsync<TResp>` per item. The
[RacerWithGrpc](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerWithGrpc) sample
uses this pattern:

```csharp
public async IAsyncEnumerable<RaceUpdate> Race(
    IAsyncEnumerable<RaceCommand> incoming,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    await foreach (var command in incoming.WithCancellation(cancellationToken))
    {
        // One Wolverine stream per incoming command.
        await foreach (var update in Bus.StreamAsync<RaceUpdate>(command, cancellationToken))
        {
            yield return update;
        }
    }
}
```

Conceptually: each inbound command opens a sub-stream that contributes updates to the outer bidi
stream. This works well when requests and responses have a **per-item correlation** (command ×
updates). It's a poor fit when you need a long-lived session where any incoming message can affect
the response ordering globally — for that, a saga + outbound messaging stays the better model.

## Cancellation

Cancellation flows top-down from the client to the handler:

1. Client disposes its `AsyncServerStreamingCall<T>` (or cancels the call).
2. gRPC propagates cancellation via `ServerCallContext.CancellationToken` / `CallContext.CancellationToken`.
3. The service shim passes that token into `Bus.InvokeAsync` / `Bus.StreamAsync`.
4. Wolverine threads it through to the handler's `CancellationToken` parameter
   (`[EnumeratorCancellation]` for streaming).

In practice this means the handler's `cancellationToken.ThrowIfCancellationRequested()` or
`await someOp(ct)` will trip as soon as the client bails, and
`OperationCanceledException` is in turn mapped to `StatusCode.Cancelled` by the exception
interceptor (see [Error Handling](./errors)).

::: warning
If your handler spawns background work via `Task.Run(...)` without passing the `CancellationToken`,
that work won't be cancelled when the client disconnects. The gRPC frame stops flowing immediately
but your detached tasks keep running. Always thread the token through.
:::

## Current limitations

- **Pure client streaming** (`stream TRequest → TResponse`) has no adapter path yet. In proto-first
  mode, a service whose `.proto` declares this shape fails fast at startup with a diagnostic
  error — it's not silently skipped. If you need this today, implement the service method by hand
  without the Wolverine shim, or reshape the contract to server streaming + a final summary
  response.
- **No `IMessageBus.StreamAsync<TRequest, TResponse>` overload.** Until that exists, bidi goes
  through the manual bridge above. Tracked as a follow-up.
- **Back-pressure is cooperative, not flow-controlled by default.** HTTP/2 provides windowing, but
  if your handler produces faster than your client consumes and your DTOs are large, memory usage
  can spike before backpressure propagates. For large payloads, consider chunking at the contract
  level (smaller messages) rather than relying on transport-level flow control alone.
- **Exception timing:** an exception thrown **before** the first `yield return` surfaces on the
  client via the trailers as expected. An exception thrown **mid-stream** surfaces as a trailer
  after messages the client has already received — well-behaved clients must still check the final
  status even after consuming messages successfully.

## Related

- [Handlers](./handlers) — where the `CancellationToken` comes from and how the service shim forwards.
- [Error Handling](./errors) — how `OperationCanceledException` becomes `StatusCode.Cancelled` and
  how to attach rich details to errors that terminate a stream.
- [Samples](./samples) — `PingPongWithGrpcStreaming` and `RacerWithGrpc` are the canonical
  streaming walkthroughs.
