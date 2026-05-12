# Wolverine

.NET distributed application framework providing in-process mediator and message bus capabilities for event-driven architectures. Part of the "critter stack" with Marten.

## Tech Stack

- **Language**: C# 12+
- **Frameworks**: .NET 9.0, 10.0 (net8.0 dropped in 6.0; 5.x maintained on the `5.0` branch)
- **Build**: MSBuild + Nuke (scripted automation)
- **Core Dependencies**: JasperFx (runtime compilation), Microsoft.Extensions.*, System.Threading.Tasks.Dataflow
- **Docs**: Vitepress (in `/docs`)

## Project Structure

```
src/
├── Wolverine/              # Core framework - message bus, handlers, middleware
├── Http/                   # HTTP/REST support
│   ├── Wolverine.Http/     # ASP.NET Core integration
│   └── Wolverine.Http.Marten/
├── Persistence/            # Storage providers
│   ├── Wolverine.RDBMS/    # Base RDBMS implementation
│   ├── Wolverine.SqlServer/
│   ├── Wolverine.Postgresql/
│   ├── Wolverine.EntityFrameworkCore/
│   ├── Wolverine.Marten/   # Event sourcing with Marten
│   └── Wolverine.RavenDb/
├── Transports/             # Message brokers
│   ├── RabbitMQ/
│   ├── AWS/                # SQS, SNS
│   ├── Azure/              # Service Bus
│   ├── Kafka/
│   ├── Redis/
│   └── [NATS, Pulsar, GCP, MQTT, SignalR]
├── Extensions/             # Serialization & validation
│   ├── Wolverine.FluentValidation/
│   ├── Wolverine.MessagePack/
│   └── Wolverine.MemoryPack/
├── Samples/                # Example applications
└── Testing/                # Test suites
    ├── CoreTests/
    ├── Wolverine.ComplianceTests/
    └── SlowTests/
```

## Build & Test

```bash
# Build (uses Nuke)
./build.sh              # macOS/Linux
build.ps1               # Windows

# Start test infrastructure
docker compose up -d    # PostgreSQL, SQL Server, RabbitMQ, Kafka, etc.

# Run tests
dotnet test             # All tests
dotnet test src/Testing/CoreTests/

# Documentation
npm install && npm run docs
```

**Solutions** (new `.slnx` XML format):
- `wolverine.slnx` - Full solution
- `wolverine_slim.slnx` - Lightweight variant

## Key Entry Points

| Concept | Location |
|---------|----------|
| Options/Config | `src/Wolverine/WolverineOptions.cs:97` |
| Message Bus | `src/Wolverine/Runtime/MessageBus.cs` |
| Message Context | `src/Wolverine/Runtime/MessageContext.cs:34` |
| Handler Discovery | `src/Wolverine/Configuration/HandlerDiscovery.cs:17` |
| Endpoint Config | `src/Wolverine/Configuration/ListenerConfiguration.cs` |
| Sagas | `src/Wolverine/Saga.cs:8` |
| HTTP Endpoints | `src/Http/Wolverine.Http/` |

## Handler Conventions

Valid handler method names: `Handle`, `HandleAsync`, `Consume`, `ConsumeAsync` (`HandlerDiscovery.cs:17-22`)

Handlers are discovered by scanning assemblies. Use attributes like `[WolverineHandler]`, `[WolverineMessage]`, `[WolverineIgnore]` to control discovery.

## Performance conventions

### Use `ImHashMap` for hot-path dictionary lookups

For any dictionary lookup where performance matters — per-message work, per-Envelope work, per-handler dispatch — use `ImHashMap<TKey, TValue>` from `JasperFx.Core`. **Do not replace `ImHashMap` with `FrozenDictionary`** even when the data is post-bootstrap-immutable.

`ImHashMap` is a copy-on-write hash trie:
- Lookups are lock-free and don't allocate.
- Writes return a new map; callers swap via `Interlocked.CompareExchange` or a plain field assignment.
- The trie structure is friendlier to the JIT for our hot paths than `FrozenDictionary`'s hash-bucket layout in practice.

If a hot path is paying for **mutation** (not lookup), the right fix is **pre-population at bootstrap** — typically inside the relevant `chain.Compile()` or `WolverineRuntime.HostService.StartAsync()` path — so steady-state runtime sees pure reads. Keep the `ImHashMap` field type.

Examples in the codebase:
- `WolverineMessageNaming._typeNames` (`src/Wolverine/Util/WolverineMessageNaming.cs:128`) — pre-populated via `PrepopulateCache(IEnumerable<Type>)` at startup.
- `HandlerGraph._chains` / `_handlers` (`src/Wolverine/Runtime/Handlers/HandlerGraph.cs:40,42`) — built once in `Compile()`.
- `Endpoint._serializers` (`src/Wolverine/Configuration/Endpoint.cs:565-581`) — currently does a hot-path `AddOrUpdate` on first miss; the right fix is to pre-populate during `Endpoint.Compile()`, not to swap the data structure.

`FrozenDictionary` may still be appropriate for **non-hot-path** snapshots, e.g. metadata exposed to user code that doesn't participate in dispatch. Default to `ImHashMap` unless you have a specific reason otherwise.

## Test conventions

### Getting an `IMessageBus` from an `IHost` in tests

`IMessageBus` is scoped per-message, so resolving it from the host's root container yields a bus that isn't wired into the active `MessageContext` and won't behave correctly under tracking, outbox, or context-propagation tests.

```csharp
// ❌ Don't — pulls the bus from the root scope, missing the per-message context
var bus = host.Services.GetRequiredService<IMessageBus>();

// ✅ Do — extension method in the `Wolverine` namespace that hands you a
// scoped bus already attached to the current message context
var bus = host.MessageBus();
```

## Configuration Organization

WolverineOptions uses partial classes to organize concerns:
- `WolverineOptions.cs` - Main options
- `WolverineOptions.Serialization.cs` - Message serialization
- `WolverineOptions.Endpoints.cs` - Endpoint configuration
- `WolverineOptions.Policies.cs` - Error handling policies
- `WolverineOptions.Assemblies.cs` - Assembly scanning

## Test Infrastructure (docker-compose.yml)

| Service | Port |
|---------|------|
| PostgreSQL | 5433 |
| SQL Server | 1434 |
| RabbitMQ | 5672 (mgmt: 15672) |
| Kafka | 9092 |
| Redis | 6379 |
| NATS | 4222 |
| LocalStack (AWS) | 4566 |
| GCP Pub/Sub | 8085 |
| Pulsar | 6650 |

## Additional Documentation

When working on specific areas, consult these files:

| Topic | File |
|-------|------|
| Architectural patterns & conventions | `.claude/docs/architectural_patterns.md` |

## Version

- **5.39.0** — last shipped on the 5.x line (see `Directory.Build.props`).
- **6.0** — `main` branch ongoing development (JasperFx 2.0-alpha line, net9.0/net10.0).
- **5.x maintenance** — bug fixes only, off the `5.0` branch.
