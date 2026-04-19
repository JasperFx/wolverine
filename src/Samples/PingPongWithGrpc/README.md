# PingPongWithGrpc

Minimal Wolverine gRPC sample using the code-first style (protobuf-net.Grpc):

- `Messages` — shared `[ServiceContract] IPingService` plus DTOs.
- `Ponger` — ASP.NET Core + Wolverine host. Discovers `PingGrpcService` by the `GrpcService` suffix convention.
- `Pinger` — console client; creates the gRPC stub from the shared interface and pings every second.

## Running

In one terminal:

```sh
dotnet run --project src/Samples/PingPongWithGrpc/Ponger --framework net9.0
```

In another:

```sh
dotnet run --project src/Samples/PingPongWithGrpc/Pinger --framework net9.0
```

Expected output on the client:

```
Pinging the Ponger every second. Ctrl+C to exit.
  <- ping 0 (handled count: 1)
  <- ping 1 (handled count: 2)
  <- ping 2 (handled count: 3)
  ...
```

The incrementing `handled count` comes from a singleton `PingTracker` on the server — it confirms every RPC lands in the Wolverine handler, not just the gRPC service method.
