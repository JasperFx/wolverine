# gRPC Endpoints

::: tip
`WolverineFx.Http.Grpc` is an **optional** add-on to `WolverineFx.Http`. It lets you expose
Wolverine message handlers over gRPC (HTTP/2 + Protocol Buffers) with the same low-ceremony,
convention-driven model you already use for HTTP endpoints.
:::

## Why gRPC?

gRPC is well-suited to **service-to-service** communication where you want:

* **High performance** — HTTP/2 multiplexing and binary Protocol Buffers serialisation
* **Strong contracts** — service definitions and message schemas are compile-time checked
* **Streaming** — bidirectional streaming is available for more advanced scenarios
* **Language interop** — gRPC clients exist for virtually every language and platform

## Installation

```bash
dotnet add package WolverineFx.Http.Grpc
```

`WolverineFx.Http.Grpc` takes a **code-first** approach using
[protobuf-net.Grpc](https://protobuf-net.github.io/protobuf-net.grpc/) — no `.proto` files required.
You define service contracts as plain C# interfaces, and Wolverine discovers and wires up the
implementations automatically.

## Concepts

| Concept | Description |
|---------|-------------|
| **Service contract** | A C# `interface` decorated with `[ServiceContract]` that describes the gRPC methods |
| **Message** | A C# class/record decorated with `[ProtoContract]` / `[ProtoMember]` (the request / response) |
| **Wolverine handler** | A regular Wolverine handler (`Handle` / `HandleAsync`) that processes the incoming message |
| **gRPC endpoint** | A class that inherits `WolverineGrpcEndpointBase`, implements the contract interface, and delegates to `Bus` |

## Quick Start

### 1. Define the shared contract

Create a **shared class library** (referenced by both client and server) that contains the service
contract and the protobuf messages:

```csharp
using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

// Protobuf-serialisable request
[ProtoContract]
public class PingMessage
{
    [ProtoMember(1)] public int Number { get; set; }
    [ProtoMember(2)] public string Message { get; set; } = string.Empty;
}

// Protobuf-serialisable response
[ProtoContract]
public class PongMessage
{
    [ProtoMember(1)] public int Number { get; set; }
    [ProtoMember(2)] public string Message { get; set; } = string.Empty;
}

// gRPC service contract
[ServiceContract]
public interface IPongerService
{
    [OperationContract]
    Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default);
}
```

### 2. Write a Wolverine handler on the server

```csharp
// Standard Wolverine handler — no gRPC knowledge required
public static class PingHandler
{
    public static PongMessage Handle(PingMessage ping, ILogger logger)
    {
        logger.LogInformation("Received Ping #{Number}", ping.Number);
        return new PongMessage { Number = ping.Number, Message = $"Pong #{ping.Number}" };
    }
}
```

### 3. Add a gRPC endpoint adapter

Create a class that bridges the gRPC contract to the Wolverine bus.
Wolverine discovers it automatically because its name ends with `GrpcEndpoint` **and** it
inherits `WolverineGrpcEndpointBase`.

```csharp
using ProtoBuf.Grpc;
using Wolverine.Http.Grpc;

public class PongerGrpcEndpoint : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
```

Alternatively, use the explicit `[WolverineGrpcService]` attribute if you prefer not to rely on
the naming convention:

```csharp
[WolverineGrpcService]
public class PongerService : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
```

### 4. Bootstrap the server

<!-- snippet: sample_grpc_ponger_bootstrapping -->
```csharp
using GrpcPonger;
using JasperFx;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Wolverine is required for WolverineFx.Http.Grpc
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Register Wolverine gRPC services (adds code-first gRPC server support)
builder.Services.AddWolverineGrpc();

var app = builder.Build();

app.UseRouting();

// Discover and map all Wolverine gRPC endpoint types (e.g., PongerGrpcEndpoint)
app.MapWolverineGrpcEndpoints();

return await app.RunJasperFxCommands(args);
```
<!-- endSnippet -->

::: tip
gRPC **requires HTTP/2**. Configure Kestrel accordingly in `appsettings.json`:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Grpc": {
        "Url": "http://localhost:5200",
        "Protocols": "Http2"
      }
    }
  }
}
```
:::

### 5. Connect from the client

```csharp
using Grpc.Net.Client;
using ProtoBuf.Grpc.Client;

using var channel = GrpcChannel.ForAddress("http://localhost:5200");
var ponger = channel.CreateGrpcService<IPongerService>();

var pong = await ponger.SendPingAsync(new PingMessage { Number = 1, Message = "Hello!" });
Console.WriteLine(pong.Message); // "Pong #1"
```

## Discovery

`MapWolverineGrpcEndpoints()` scans the same assemblies that Wolverine uses for handler discovery
(your entry assembly plus any additional ones registered via `UseWolverine`). A type is treated
as a Wolverine gRPC endpoint when **all** of the following are true:

1. It is a **public, concrete, non-generic** class
2. It **inherits** `WolverineGrpcEndpointBase`
3. It is decorated with `[WolverineGrpcService]` **OR** its name ends with one of:
   `GrpcEndpoint`, `GrpcEndpoints`, `GrpcService`, `GrpcServices`

You can add extra assemblies to the scan:

```csharp
builder.Services.AddWolverineGrpc(opts =>
{
    opts.Assemblies.Add(typeof(SomeTypeInAnotherAssembly).Assembly);
});
```

## Using with Wolverine.Http together

`WolverineFx.Http.Grpc` works side-by-side with `WolverineFx.Http`. You can expose some
endpoints via HTTP/REST and others via gRPC within the same application:

```csharp
builder.Services.AddWolverineHttp();
builder.Services.AddWolverineGrpc();

// ...

app.MapWolverineEndpoints();        // REST endpoints
app.MapWolverineGrpcEndpoints();    // gRPC endpoints
```

## Sample Project

A complete PingPong sample demonstrating two services communicating over gRPC can be found at
`src/Samples/PingPongWithGrpc` in the Wolverine repository.

* **GrpcPonger** — the server that listens for Ping messages and responds with Pong via gRPC
* **GrpcPinger** — the client that sends Ping messages every second
* **Contracts** — the shared service contract and protobuf message definitions

## Considerations

### TLS / HTTPS

For production use, configure Kestrel with a TLS certificate and use `https://` URLs.
In development you can use unencrypted HTTP/2 (`http://`) for simplicity, but make sure
the gRPC client is configured to allow plain-text connections:

```csharp
// Development only — do not use in production without TLS!
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
var channel = GrpcChannel.ForAddress("http://localhost:5200");
```

### Dependency Injection and Lifetime

`WolverineGrpcEndpointBase` exposes a settable `Bus` property (`IMessageBus`) that is populated
by ASP.NET Core's dependency injection at request time. gRPC service types are resolved per
request (transient / scoped) by default, which aligns with how Wolverine handlers work.

### Error handling

gRPC translates exceptions thrown inside service methods into gRPC status codes (e.g.,
`StatusCode.Internal` for unhandled exceptions). For domain validation errors you can throw
`RpcException` directly with an appropriate status code.

### Streaming

Unary (request/response) calls are fully supported. Server-streaming, client-streaming, and
bidirectional streaming are also possible through `IAsyncEnumerable<T>` return types in
protobuf-net.Grpc — Wolverine handlers can return streaming responses if needed.
