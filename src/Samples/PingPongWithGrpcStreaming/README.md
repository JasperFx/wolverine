# PingPongWithGrpcStreaming

Server-streaming counterpart to `PingPongWithGrpc`. The contract returns
`IAsyncEnumerable<PongReply>`, which protobuf-net.Grpc recognises as server
streaming. The handler yields one `PongReply` per iteration and Wolverine's
`IMessageBus.StreamAsync<T>` plumbs it through.

- `Messages` — shared `[ServiceContract] IPingStreamService` + DTOs.
- `Ponger` — ASP.NET Core + Wolverine host with a streaming handler.
- `Pinger` — requests a stream of 5 pongs every 3 seconds.

## Running

In one terminal:

```sh
dotnet run --project src/Samples/PingPongWithGrpcStreaming/Ponger --framework net9.0
```

In another:

```sh
dotnet run --project src/Samples/PingPongWithGrpcStreaming/Pinger --framework net9.0
```

Expected output on the client:

```
Requesting a stream of 5 pongs every 3 seconds. Ctrl+C to exit.
-- round 0 --
  <- round-0:0 (handled count: 1)
  <- round-0:1 (handled count: 2)
  <- round-0:2 (handled count: 3)
  <- round-0:3 (handled count: 4)
  <- round-0:4 (handled count: 5)
-- round 1 --
  <- round-1:0 (handled count: 6)
  ...
```

The handled-count keeps incrementing across rounds, confirming each yielded
item corresponds to a real handler pass rather than being batched.
