# GreeterCodeFirstGrpc

Code-first (protobuf-net.Grpc) Wolverine gRPC sample with a **generated
implementation**. There is no `.proto` file and no hand-written service class.
The `[ServiceContract]` interface in `Messages` carries no Wolverine attribute
and the `Messages` project references only `protobuf-net.Grpc`; the server
registers the contract with
`AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IGreeterCodeFirstService>())`,
which is the only instruction Wolverine needs to generate
`GreeterCodeFirstServiceGrpcHandler` at startup and forward each RPC to the bus.
That keeps `WolverineFx.Grpc` and the ASP.NET Core gRPC hosting stack out of
every client that binds the interface (GH-4396). Putting `[WolverineGrpcService]`
on the interface instead is equivalent when the contracts project can afford
the reference.

Covers a unary RPC, a server-streaming RPC, and a client-streaming RPC — all
three generated code-first shapes.

- `Messages` — the `IGreeterCodeFirstService` contract and its
  `[ProtoContract]` DTOs, shared by server and client. Depends on
  `protobuf-net.Grpc` only.
- `Server` — ASP.NET Core + Wolverine host. Registers the contract with
  `IncludeCodeFirstContract<T>()`. Handlers are plain Wolverine handlers with
  no gRPC coupling.
- `Client` — console client using protobuf-net.Grpc's
  `channel.CreateGrpcService<IGreeterCodeFirstService>()` proxy.

## Running

In one terminal:

```sh
dotnet run --project src/Samples/GreeterCodeFirstGrpc/Server --framework net9.0
```

In another:

```sh
dotnet run --project src/Samples/GreeterCodeFirstGrpc/Client --framework net9.0
```

Expected output on the client:

```
Greet       -> Hello, Erik!
StreamGreetings ->
  Hello, Erik [1 of 5]
  Hello, Erik [2 of 5]
  Hello, Erik [3 of 5]
  Hello, Erik [4 of 5]
  Hello, Erik [5 of 5]
CollectGreetings -> Hello, Erik & Ripley & Newt (3 greetings)
```

The `CollectGreetings` line demonstrates client streaming on the code-first
path: the client hands the interface proxy a plain
`IAsyncEnumerable<GreetRequest>` (no request-stream writer plumbing), and the
generated implementation forwards that stream — protobuf-net.Grpc exposes it as
`IAsyncEnumerable<GreetRequest>` directly, so no adapter is involved — to
`IMessageBus.StreamAsync<GreetRequest, GreetingSummary>`, where a Wolverine
handler folds it into a single `GreetingSummary` reply.
