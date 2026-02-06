# Wolverine

.NET distributed application framework providing in-process mediator and message bus capabilities for event-driven architectures. Part of the "critter stack" with Marten.

## Tech Stack

- **Language**: C# 12+
- **Frameworks**: .NET 8.0, 9.0, 10.0
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

**Solutions**:
- `wolverine.sln` - Full solution
- `wolverine_slim.sln` - Lightweight variant

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

Current: **5.13.0** (see `Directory.Build.props`)
