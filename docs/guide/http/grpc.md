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
lets you define gRPC contracts as plain C# interfaces — no `.proto` files required.
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

#### 3. Write the gRPC service class

There are three equivalent ways to write the endpoint class. Pick whichever best fits your style:

**Convention-based discovery (inherits `WolverineGrpcEndpointBase`)**

Wolverine discovers the class automatically because its name ends with `GrpcEndpoint` **and** it
inherits `WolverineGrpcEndpointBase`. The `Bus` property is populated by ASP.NET Core's DI at
request time.

```csharp
using ProtoBuf.Grpc;
using Wolverine.Http.Grpc;

public class PongerGrpcEndpoint : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
```

**Attribute-based discovery (inherits `WolverineGrpcEndpointBase`)**

Use `[WolverineGrpcService]` when you want attribute-driven discovery without relying on the naming
convention. `IMessageBus` is still obtained from the `Bus` property on the base class.

```csharp
[WolverineGrpcService]
public class PongerService : WolverineGrpcEndpointBase, IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => Bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
```

**Attribute-based discovery with constructor injection (no base class)**

When you prefer constructor injection — or when your class must inherit a different base class
(e.g., a proto-generated `ServiceName.ServiceNameBase`) — omit `WolverineGrpcEndpointBase`
entirely and inject `IMessageBus` via the constructor. The attribute alone is sufficient for
automatic discovery.

```csharp
[WolverineGrpcService]
public class PongerService : IPongerService
{
    private readonly IMessageBus _bus;

    public PongerService(IMessageBus bus) => _bus = bus;

    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => _bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
```

::: tip
**C# 12+ Developers**: You can use **primary constructors** to reduce boilerplate when using constructor injection:

```csharp
[WolverineGrpcService]
public class PongerService(IMessageBus bus) : IPongerService
{
    public Task<PongMessage> SendPingAsync(PingMessage request, CallContext context = default)
        => bus.InvokeAsync<PongMessage>(request, context.CancellationToken);
}
```

This is especially useful for proto-first services where constructor injection is required.
:::

::: tip
Constructor injection is also **required** for proto-first services (see the [Proto-First Approach](#proto-first-approach-proto-files) section below), because proto-generated services must inherit `ServiceName.ServiceNameBase` and C# does not allow multiple inheritance.
:::

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

### Code-First Sample Projects

A complete PingPong sample demonstrating two services communicating over gRPC can be found in
[`src/Samples/PingPongWithGrpc`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/PingPongWithGrpc):

* **GrpcPonger** — the server that listens for `PingMessage` and responds with `PongMessage` over gRPC
* **GrpcPinger** — the client that sends `PingMessage` requests every second
* **Contracts** — the shared service contract and protobuf message definitions

---

## Proto-First Approach (`.proto` files)

The proto-first approach uses `.proto` files as the source of truth for service contracts.
The `Grpc.Tools` NuGet package generates C# types (request/reply classes and a service base
class) from the `.proto` file at build time.
This is the preferred pattern for enterprises and cross-language teams that publish a single
`.proto`-based NuGet package so both .NET servers and non-.NET clients share the same schema.

::: tip
A complete proto-first sample project can be found in
[`src/Samples/ProtoFirstGrpcSample`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/ProtoFirstGrpcSample).
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

::: tip
**C# 12+ alternative using primary constructors**:

```csharp
[WolverineGrpcService]
public class GreeterService(IMessageBus bus) : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
```

Primary constructors eliminate the need for explicit field declarations and assignment, reducing boilerplate.
:::

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

### Code-First vs. Proto-First comparison

| Feature | Code-first (convention or attribute + base class) | Code-first (attribute + constructor injection) | Proto-first |
|--|-----------|-------------|-------------|
| Contract definition | C# interface + `[ServiceContract]` | C# interface + `[ServiceContract]` | `.proto` file |
| C# type generation | None (already C#) | None (already C#) | `Grpc.Tools` at build time |
| Cross-language support | Limited | Limited | Full |
| Base class | `WolverineGrpcEndpointBase` | None required | Proto-generated `ServiceName.ServiceNameBase` |
| `IMessageBus` access | `Bus` property (property injection) | Constructor injection | Constructor injection |
| Auto-discovery | Attribute OR naming convention | `[WolverineGrpcService]` attribute required | `[WolverineGrpcService]` attribute required |
| NuGet sharing | Share the C# contracts assembly | Share the C# contracts assembly | Share the `.proto` file (or generated assembly) |

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

## Code Generation and Performance

Wolverine.Http.Grpc uses **runtime code generation** to create optimized handler implementations for your gRPC services. This eliminates dependency injection boilerplate and improves performance by generating specialized adapters at startup.

### How it works

When you call `MapWolverineGrpcEndpoints()`, Wolverine:

1. **Discovers** all eligible gRPC service types via the `GrpcGraph`
2. **Analyzes** each service to determine if code generation is needed
3. **Generates** optimized handler code using JasperFx's code generation framework
4. **Compiles** the generated code into a runtime assembly
5. **Registers** the generated types with ASP.NET Core's DI container

### When code generation happens

Code generation is **selective** and only occurs when beneficial:

| Service pattern | Code generation? | Reason |
|----------------|------------------|--------|
| Inherits `WolverineGrpcEndpointBase` | ❌ No | Already has `Bus` property; no optimization needed |
| Constructor injection with `IMessageBus` | ❌ No | Standard DI pattern; no boilerplate to eliminate |
| Abstract proto-first service with `[WolverineGrpcService]` | ✅ Yes | Generates concrete implementation delegating to `IMessageBus` |

::: info
Most common patterns (inheriting `WolverineGrpcEndpointBase` or using constructor injection) **do not trigger code generation** because they already have efficient implementations. Code generation is primarily used for advanced scenarios like abstract service base classes.
:::

### Viewing generated code

To inspect the generated handler code for debugging or understanding:

#### Option 1: Use the preview mode

Enable code generation preview mode in your `Program.cs`:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;

    // Write generated code to console/logs
    opts.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Auto;
    opts.CodeGeneration.SourceCodeWritingEnabled = true;
});
```

#### Option 2: Pre-generate code files

Use Wolverine's code generation command to write the generated code to disk:

```bash
dotnet run -- codegen write
```

This creates `.cs` files in the `Internal/Generated` directory of your project. You can inspect these files to see exactly what Wolverine generated for your gRPC services.

::: tip
Pre-generating code is also useful for **production deployments** where you want to eliminate startup time spent on code generation and ensure deterministic behavior.
:::

#### Option 3: Check logs

Wolverine logs code generation activity at the `Debug` and `Information` levels:

```json
{
  "Logging": {
    "LogLevel": {
      "Wolverine.Http.Grpc.GrpcGraph": "Debug"
    }
  }
}
```

Look for log messages like:
- `Creating GrpcChain for service type: YourNamespace.YourService`
- `Discovered N gRPC service(s) for code generation`

## Considerations

### TLS / HTTPS

gRPC runs over HTTP/2, which almost always requires TLS in production.  No Wolverine-specific API
is needed — you configure Kestrel exactly as the
[ASP.NET Core TLS documentation](https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore#tls)
describes.

**Development** — the ASP.NET Core development certificate is sufficient.  When you create a
project with `dotnet new web`, the default profile uses `https://` automatically.  For plain
HTTP/2 (useful if TLS adds friction during early development) add the switch to the **client**:

```csharp
// Development only — do not use in production without TLS!
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
var channel = GrpcChannel.ForAddress("http://localhost:5300");
```

And configure the Kestrel listener to use plain HTTP/2 on the server (`appsettings.json`):

```json
{
  "Kestrel": {
    "Endpoints": {
      "Grpc": {
        "Url": "http://localhost:5300",
        "Protocols": "Http2"
      }
    }
  }
}
```

**Production (appsettings.json)** — specify the certificate and restrict to HTTP/2:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Grpc": {
        "Url": "https://0.0.0.0:5300",
        "Protocols": "Http2",
        "Certificate": {
          "Path": "/etc/ssl/certs/grpc.pfx",
          "Password": "<your-password>"
        }
      }
    }
  }
}
```

**Production (`Program.cs`)** — alternatively configure Kestrel in code:

```csharp
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Any, 5300, listen =>
    {
        listen.Protocols = HttpProtocols.Http2;
        listen.UseHttps("/etc/ssl/certs/grpc.pfx", "<your-password>");
    });
});
```

::: tip
`WolverineFx.Http.Grpc` does not add its own TLS fluent API — you use the standard
ASP.NET Core / Kestrel configuration surfaces above.  This keeps the security surface small and
aligned with Microsoft's official guidance.
:::

### Authorization

gRPC services hosted by Wolverine are standard ASP.NET Core services and support all of the
same authorization primitives:

**Requiring authentication on a single method** — add `[Authorize]` to the endpoint class or to
a specific gRPC method implementation.  The framework checks the attribute before the method body
runs:

```csharp
using Microsoft.AspNetCore.Authorization;

[WolverineGrpcService]
public class SecureOrderService : WolverineGrpcEndpointBase, IOrderService
{
    [Authorize]                           // JWT / bearer token required
    public Task<OrderReply> PlaceOrderAsync(OrderRequest request, CallContext context = default)
        => Bus.InvokeAsync<OrderReply>(request, context.CancellationToken);
}
```

**Bootstrap** — enable ASP.NET Core authentication before `MapWolverineGrpcEndpoints`:

```csharp
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();

// ...

app.UseAuthentication();
app.UseAuthorization();
app.MapWolverineGrpcEndpoints();
```

**Client** — attach a bearer token to every call via `CallCredentials`:

```csharp
var credentials = CallCredentials.FromInterceptor((context, metadata) =>
{
    metadata.Add("Authorization", $"Bearer {myJwtToken}");
    return Task.CompletedTask;
});

var channel = GrpcChannel.ForAddress("https://localhost:5300", new GrpcChannelOptions
{
    Credentials = ChannelCredentials.Create(new SslCredentials(), credentials)
});
```

For a full walkthrough of gRPC JWT authentication in ASP.NET Core, see the
[Microsoft Ticketer example](https://github.com/grpc/grpc-dotnet/tree/master/examples/Ticketer).

### Dependency Injection and Lifetime

**Code-first** (`WolverineGrpcEndpointBase`): Exposes a settable `Bus` property (`IMessageBus`)
that is populated by ASP.NET Core's dependency injection at request time.

**Constructor injection**: `IMessageBus` is injected via the constructor, which
is the standard ASP.NET Core DI pattern. Both patterns result in the same per-request lifetime.

gRPC service types are resolved per request (transient / scoped) by default, which aligns with
how Wolverine handlers work.

### Error handling

gRPC translates exceptions thrown inside service methods into gRPC status codes (e.g.,
`StatusCode.Internal` for unhandled exceptions). For domain validation errors you can throw
`RpcException` directly with an appropriate status code.

### Streaming

Unary (request/response) calls are fully supported.  **Server-streaming**, **client-streaming**,
and **bidirectional (duplex) streaming** are all available through `IAsyncEnumerable<T>` in
protobuf-net.Grpc:

| Pattern | Contract signature |
|---|---|
| Unary | `Task<TReply> MethodAsync(TRequest req, CallContext ctx = default)` |
| Server streaming | `IAsyncEnumerable<TReply> MethodAsync(TRequest req, CallContext ctx = default)` |
| Client streaming | `Task<TReply> MethodAsync(IAsyncEnumerable<TRequest> reqs, CallContext ctx = default)` |
| Bidirectional | `IAsyncEnumerable<TReply> MethodAsync(IAsyncEnumerable<TRequest> reqs, CallContext ctx = default)` |

#### Streaming with IMessageBus.StreamAsync (Recommended Pattern)

Wolverine supports **streaming handlers** that return `IAsyncEnumerable<T>` and can be invoked via
`IMessageBus.StreamAsync<TResponse>()`. This pattern enables streaming through Wolverine's full
middleware pipeline with automatic OpenTelemetry instrumentation.

**Key benefits:**
- ✅ **Separation of concerns** - gRPC endpoint handles protocol, handler contains business logic
- ✅ **Middleware pipeline** - Automatic telemetry, cascading messages, side effects, error policies
- ✅ **Testability** - Test handlers independently without gRPC infrastructure
- ✅ **Reusability** - Same handler can be invoked via gRPC, HTTP, message bus, or scheduled jobs

**Example — bidirectional streaming service contract:**

```csharp
[ServiceContract]
public interface IRacingService
{
    [OperationContract]
    IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default);
}
```

**Example — Wolverine streaming handler** (business logic returns `IAsyncEnumerable<T>`):

```csharp
public class RaceStreamHandler
{
    private readonly ConcurrentDictionary<string, double> _speeds = new();

    public async IAsyncEnumerable<RacePosition> Handle(RacerUpdate update)
    {
        _speeds[update.RacerId] = update.Speed;

        var standings = _speeds
            .OrderByDescending(kv => kv.Value)
            .Select((kv, idx) => new RacePosition
            {
                RacerId = kv.Key,
                Position = idx + 1,
                Speed = kv.Value
            })
            .ToList();

        var position = standings.FirstOrDefault(p => p.RacerId == update.RacerId);
        if (position is not null)
        {
            yield return position;
        }

        await Task.Delay(1); // Simulate async work
    }
}
```

**Example — gRPC endpoint delegating to Wolverine handler** (thin protocol adapter):

```csharp
public class RacingGrpcService : WolverineGrpcEndpointBase, IRacingService
{
    public async IAsyncEnumerable<RacePosition> RaceAsync(
        IAsyncEnumerable<RacerUpdate> updates,
        CallContext context = default)
    {
        // For each incoming update, invoke the Wolverine streaming handler
        // This provides automatic OpenTelemetry instrumentation and middleware pipeline execution
        await foreach (var update in updates.WithCancellation(context.CancellationToken))
        {
            await foreach (var position in Bus.StreamAsync<RacePosition>(update, context.CancellationToken))
            {
                yield return position;
            }
        }
    }
}
```

**Example — bidirectional streaming client:**

```csharp
using var channel = GrpcChannel.ForAddress("http://localhost:5300");
var racing = channel.CreateGrpcService<IRacingService>();

async IAsyncEnumerable<RacerUpdate> ProduceUpdates(
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        yield return new RacerUpdate { RacerId = "Racer-A", Speed = 175.3 };
        await Task.Delay(200, cancellationToken);
    }
}

await foreach (var position in racing.RaceAsync(ProduceUpdates(cts.Token), cts.Token))
{
    Console.WriteLine($"{position.RacerId} is in position {position.Position}");
}
```

A full working bidirectional streaming sample demonstrating `IMessageBus.StreamAsync` integration is available in
[`src/Samples/RacerGrpcSample`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/RacerGrpcSample).

::: tip
For simple inline streaming logic that doesn't need middleware pipeline execution, you can implement streaming
directly in the gRPC endpoint method without delegating to a handler. However, the recommended pattern is to
use `Bus.StreamAsync()` to leverage Wolverine's middleware pipeline, telemetry, and architectural benefits.
:::

---

## FAQ: Proto-First Services

### Why must proto-first services inherit the proto-generated base class?

The proto-generated base class (e.g., `Greeter.GreeterBase`) is **required by ASP.NET Core's gRPC infrastructure**.
It contains the gRPC method signatures and infrastructure integration that the framework needs to route
requests to your implementation. There is **no way to remove it or replace it** — this is a gRPC/ASP.NET Core
framework requirement, not a Wolverine limitation.

### Why can't proto-first services inherit WolverineGrpcEndpointBase?

C# **does not support multiple inheritance**. Since proto-first services must inherit from the proto-generated
base class (e.g., `Greeter.GreeterBase`), they cannot also inherit from `WolverineGrpcEndpointBase`.
This is a language constraint, not a design choice.

### Why is [WolverineGrpcService] required for proto-first services?

The `[WolverineGrpcService]` attribute is the **only way** to discover proto-first services automatically.
Convention-based discovery (which looks for classes ending in `GrpcService`, `GrpcEndpoint`, etc.) requires
`WolverineGrpcEndpointBase` as a safety guard to avoid false positives. Since proto-first services can't
inherit that base class, the attribute is mandatory for discovery.

### Do I always need to inject IMessageBus via constructor?

**No, only if you actually use it**. If you call `_bus.InvokeAsync()` or other message bus methods in your
service implementation, then you need the field and constructor parameter. If you handle requests inline
without delegating to Wolverine handlers, you can omit both the field and constructor parameter entirely.

**Example with IMessageBus (delegating to handlers):**
```csharp
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    private readonly IMessageBus _bus;

    public GreeterService(IMessageBus bus) => _bus = bus;

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => _bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
```

**Example without IMessageBus (inline handling):**
```csharp
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => Task.FromResult(new HelloReply { Message = $"Hello, {request.Name}!" });
}
```

### Can proto-first services use property injection like code-first services?

No. Property injection (the `Bus` property on `WolverineGrpcEndpointBase`) only works if you inherit that
base class. Since proto-first services must inherit the proto-generated base class instead, you **must use
constructor injection** for any dependencies you need, including `IMessageBus`.

---

## Design Notes: The `WolverineGrpcEndpointBase` Requirement

::: info
This section explains why `WolverineGrpcEndpointBase` exists, when it is and isn't required, and
the trade-offs behind the current design.
:::

### Why `WolverineGrpcEndpointBase` exists

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

### The attribute-based path already relaxes the constraint

As of this release, the `[WolverineGrpcService]` **attribute bypasses the base class check** —
proto-first services only need the attribute and constructor injection of `IMessageBus`.
This means the base class is effectively optional for any service that uses the attribute,
regardless of whether it is code-first or proto-first.

### Why convention-based discovery still requires it

Removing the base class entirely from **naming-convention discovery** would require
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
Full source-generator support remains a future possibility.
