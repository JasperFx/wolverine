# GreeterProtoFirstGrpc

Proto-first Wolverine gRPC sample. `greeter.proto` is the source of truth and
`Grpc.Tools` generates the client + server stubs. The server supplies a single
abstract `[WolverineGrpcService] abstract class GreeterGrpcService : Greeter.GreeterBase`
— Wolverine generates the concrete subclass at startup that forwards each RPC
to the bus.

Covers unary RPCs, a server-streaming RPC, and the AIP-193
exception-to-StatusCode mapping.

- `Messages` — `greeter.proto` (`GrpcServices="Both"` generates both sides).
- `Server` — ASP.NET Core + Wolverine host. Handlers are plain Wolverine
  handlers with no gRPC coupling.
- `Client` — console client using the generated `Greeter.GreeterClient`.

## Running

In one terminal:

```sh
dotnet run --project src/Samples/GreeterProtoFirstGrpc/Server --framework net9.0
```

In another:

```sh
dotnet run --project src/Samples/GreeterProtoFirstGrpc/Client --framework net9.0
```

Expected output on the client:

```
SayHello -> Hello, Erik
SayGoodbye -> Goodbye, Erik
StreamGreetings ->
  Hello, Erik [0]
  Hello, Erik [1]
  Hello, Erik [2]
  Hello, Erik [3]
  Hello, Erik [4]
Fault('key') -> RpcException: NotFound (missing key)
```

The `Fault('key')` line comes from a handler that throws
`KeyNotFoundException`. The built-in interceptor maps it to
`StatusCode.NotFound` per AIP-193 — no user code translates between the
.NET exception and the gRPC status.
