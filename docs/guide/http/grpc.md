# gRPC Endpoints

::: tip
`WolverineFx.Http.Grpc` is an **optional** add-on to `WolverineFx.Http`. It lets you expose
Wolverine message handlers over gRPC (HTTP/2 + Protocol Buffers) with the same low-ceremony,
convention-driven model you already use for HTTP endpoints.
:::

## Why gRPC?

gRPC is well-suited to **service-to-service** communication where you want:

* **High performance** — HTTP/2 multiplexing and binary Protocol Buffers serialization
* **Strong contracts** — service definitions and message schemas are compile-time checked
* **Streaming** — bidirectional streaming is available for more advanced scenarios
* **Language interop** — gRPC clients exist for virtually every language and platform

For a comprehensive overview of gRPC in ASP.NET Core, see the
[official Microsoft documentation](https://learn.microsoft.com/en-us/aspnet/core/grpc/).

## Installation

```bash
dotnet add package WolverineFx.Http.Grpc
```

`WolverineFx.Http.Grpc` supports **two approaches** for defining gRPC service contracts:

| Approach | Package | Best for |
|----------|---------|----------|
| **Code-first** | `protobuf-net.Grpc` | Green-field .NET-only services; no `.proto` files required |
| **Proto-first** | `Grpc.AspNetCore` + `Grpc.Tools` | Enterprise/cross-language teams that share `.proto` contracts as NuGet packages |

Both approaches integrate with Wolverine's message bus in exactly the same way.

## Code-First Approach

The code-first approach, built on [protobuf-net.Grpc](https://protobuf-net.github.io/protobuf-net.grpc/),
lets you define gRPC contracts as plain C# interfaces.
No `.proto` files are required.
See the [Microsoft code-first gRPC documentation](https://learn.microsoft.com/en-us/aspnet/core/grpc/code-first)
for background.

### Concepts

| Concept | Description |
|---------|-------------|
| **Service contract** | A C# `interface` decorated with `[ServiceContract]` that describes the gRPC methods |
| **Message** | A C# class/record decorated with `[ProtoContract]` / `[ProtoMember]` (the request / response) |
| **Wolverine handler** | A regular Wolverine handler (`Handle` / `HandleAsync`) that processes the incoming message |
| **gRPC endpoint** | A class that inherits `WolverineGrpcEndpointBase`, implements the contract interface, and delegates to `Bus` |

### Quick Start

#### 1. Define the shared contract

Create a **shared class library** (referenced by both client and server) that contains the service
contract and the protobuf messages:

```csharp
using ProtoBuf;
using ProtoBuf.Grpc;
using System.ServiceModel;

// Protobuf-serializable request
[ProtoContract]
public class PingMessage
{
    [ProtoMember(1)] public int Number { get; set; }
    [ProtoMember(2)] public string Message { get; set; } = string.Empty;
}

// Protobuf-serializable response
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

#### 2. Write a Wolverine handler on the server

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

#### 3. Add a gRPC endpoint adapter

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

#### 4. Bootstrap the server

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

#### 5. Connect from the client

```csharp
using Grpc.Net.Client;
using ProtoBuf.Grpc.Client;

using var channel = GrpcChannel.ForAddress("http://localhost:5200");
var ponger = channel.CreateGrpcService<IPongerService>();

var pong = await ponger.SendPingAsync(new PingMessage { Number = 1, Message = "Hello!" });
Console.WriteLine(pong.Message); // "Pong #1"
```

### Code-First Sample Project

A complete PingPong sample demonstrating two services communicating over gRPC can be found at
`src/Samples/PingPongWithGrpc` in the Wolverine repository.

* **GrpcPonger** — the server that listens for Ping messages and responds with Pong via gRPC
* **GrpcPinger** — the client that sends Ping messages every second
* **Contracts** — the shared service contract and protobuf message definitions

---

## Proto-First Approach (`.proto` files)

The proto-first approach uses `.proto` files as the source of truth for service contracts.
The `Grpc.Tools` NuGet package generates C# types (request/reply classes and a service base
class) from the `.proto` file at build time.
This is the preferred pattern for enterprises and cross-language teams that publish a single
`.proto`-based NuGet package so both .NET servers and non-.NET clients share the same schema.

::: tip
A complete proto-first sample project can be found at `src/Samples/ProtoFirstGrpcSample` in
the Wolverine repository.
:::

### How it works with Wolverine

With the proto-first approach:

1. The service **does not** inherit `WolverineGrpcEndpointBase`; it inherits the
   proto-generated `<ServiceName>.<ServiceName>Base` class (e.g. `Greeter.GreeterBase`).
2. `IMessageBus` is injected via the **constructor** (standard ASP.NET Core DI) rather than
   via the `Bus` property on the base class.
3. The `[WolverineGrpcService]` **attribute is required** for automatic discovery because
   the naming-convention discovery path still requires `WolverineGrpcEndpointBase`.

### Quick Start

#### 1. Create the contracts project with the `.proto` file

Add a shared class library with `Grpc.Tools` to generate C# types from `greeter.proto`:

```xml
<!-- ProtoContracts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Google.Protobuf"/>
    <PackageReference Include="Grpc.Tools" PrivateAssets="all"/>
    <PackageReference Include="Grpc.AspNetCore"/>
    <!-- GrpcServices="Both" generates both client stub and server base class -->
    <Protobuf Include="Protos\greeter.proto" GrpcServices="Both"/>
  </ItemGroup>
</Project>
```

```proto
// Protos/greeter.proto
syntax = "proto3";
option csharp_namespace = "ProtoContracts";
package greeter;

service Greeter {
  rpc SayHello (HelloRequest) returns (HelloReply);
}

message HelloRequest { string name = 1; }
message HelloReply   { string message = 1; }
```

#### 2. Implement the gRPC service using the generated base class

```csharp
using Grpc.Core;
using ProtoContracts;
using Wolverine;
using Wolverine.Http.Grpc;

// [WolverineGrpcService] enables automatic discovery by MapWolverineGrpcEndpoints().
// The base class is the proto-generated Greeter.GreeterBase, NOT WolverineGrpcEndpointBase.
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    private readonly IMessageBus _bus;

    public GreeterService(IMessageBus bus) => _bus = bus;

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => _bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
```

#### 3. Write the Wolverine handler

```csharp
using ProtoContracts;

public class SayHelloHandler
{
    public static HelloReply Handle(HelloRequest request)
        => new HelloReply { Message = $"Hello, {request.Name}!" };
}
```

#### 4. Bootstrap the server

<!-- snippet: sample_proto_first_grpc_server_bootstrapping -->
```csharp
using JasperFx;
using ProtoFirstServer;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Wolverine is required for WolverineFx.Http.Grpc
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Register Wolverine gRPC services.
// Because this is proto-first, AddWolverineGrpc() still calls services.AddGrpc()
// under the covers, which is required by Grpc.AspNetCore.
builder.Services.AddWolverineGrpc();

var app = builder.Build();

app.UseRouting();

// Discover and map all types decorated with [WolverineGrpcService].
// GreeterService is discovered here even though it inherits from the proto-generated
// Greeter.GreeterBase rather than WolverineGrpcEndpointBase, because the
// [WolverineGrpcService] attribute enables attribute-based discovery without
// requiring the base class.
app.MapWolverineGrpcEndpoints();

return await app.RunJasperFxCommands(args);
```
<!-- endSnippet -->

### Code-First vs Proto-First comparison

| | Code-First | Proto-First |
|--|-----------|-------------|
| Contract definition | C# interface + `[ServiceContract]` | `.proto` file |
| C# type generation | None (already C#) | `Grpc.Tools` at build time |
| Cross-language support | Limited (protobuf schema can be exported) | Full (`.proto` file is language-neutral) |
| Base class | `WolverineGrpcEndpointBase` | Proto-generated `ServiceName.ServiceNameBase` |
| `IMessageBus` access | `Bus` property (property injection) | Constructor injection |
| Auto-discovery | Attribute OR naming convention | `[WolverineGrpcService]` attribute required |
| NuGet sharing | Share the C# contracts assembly | Share the `.proto` file (or generated assembly) |

---

## Discovery

`MapWolverineGrpcEndpoints()` scans the same assemblies that Wolverine uses for handler discovery
(your entry assembly plus any additional ones registered via `UseWolverine`). A type is treated
as a Wolverine gRPC endpoint when **all** of the following are true:

1. It is a **public, concrete, non-generic** class
2. **Either** it is decorated with `[WolverineGrpcService]` *(base class not required)*
3. **Or** it inherits `WolverineGrpcEndpointBase` AND its name ends with one of:
   `GrpcEndpoint`, `GrpcEndpoints`, `GrpcService`, `GrpcServices`

::: warning
The `[WolverineGrpcService]` attribute is the only way to auto-discover **proto-first** services.
The naming-convention path still requires `WolverineGrpcEndpointBase` to avoid false positives.
:::

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

**Code-first** (`WolverineGrpcEndpointBase`): Exposes a settable `Bus` property (`IMessageBus`)
that is populated by ASP.NET Core's dependency injection at request time.

**Proto-first** (constructor injection): `IMessageBus` is injected via the constructor, which
is the standard ASP.NET Core DI pattern. Both patterns result in the same per-request lifetime.

gRPC service types are resolved per request (transient / scoped) by default, which aligns with
how Wolverine handlers work.

### Error handling

gRPC translates exceptions thrown inside service methods into gRPC status codes (e.g.,
`StatusCode.Internal` for unhandled exceptions). For domain validation errors you can throw
`RpcException` directly with an appropriate status code.

### Streaming

Unary (request/response) calls are fully supported. Server-streaming, client-streaming, and
bidirectional streaming are also possible through `IAsyncEnumerable<T>` return types in
protobuf-net.Grpc — Wolverine handlers can return streaming responses if needed.

---

## Roadmap: Eliminating the base class requirement

::: info
This section documents the current state of the `WolverineGrpcEndpointBase` requirement and
the planned evolution toward making it optional for all discovery paths.
:::

### Why `WolverineGrpcEndpointBase` exists today

`WolverineGrpcEndpointBase` is a thin bridge class that exposes a single property:

```csharp
public abstract class WolverineGrpcEndpointBase
{
    public IMessageBus Bus { get; set; } = null!;
}
```

ASP.NET Core's DI container populates `Bus` at request time via property injection.
The base class is required by the **naming-convention** discovery path to avoid accidentally
registering unrelated classes whose names happen to end with `GrpcService`.

### The proto-first work already relaxes the constraint

As of this release, the `[WolverineGrpcService]` **attribute bypasses the base class check** —
proto-first services only need the attribute and constructor injection of `IMessageBus`.
This means the base class is effectively optional for any service that uses the attribute,
regardless of whether it is code-first or proto-first.

### Why full elimination is non-trivial

Removing the base class entirely from **code-first convention-based discovery** would require
one of:

1. **Roslyn Source Generators** — generate the gRPC service class at compile time from an
   annotated Wolverine handler or interface declaration. Viable, but requires significant
   engineering investment and a new `WolverineFx.Http.Grpc.SourceGen` package.

2. **Runtime dynamic proxies** — generate a `DispatchProxy`-based wrapper at startup that
   implements the gRPC service contract and delegates to `IMessageBus`. Viable at runtime,
   but adds complexity and makes the generated code invisible to the developer.

3. **Convention discovery without base class** — relax the naming-convention guard to allow
   any class with the right name suffix. Risky without strong type guards (any class named
   `CustomerGrpcService` would be discovered even if it is not a gRPC service at all).

The current approach (attribute removes the base class requirement; convention retains it as
a safety net) provides the best balance of ergonomics, discoverability, and safety.
Full source-generator support is tracked as a future enhancement.
