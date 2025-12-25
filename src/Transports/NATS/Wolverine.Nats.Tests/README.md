# NATS Transport Tests

## Prerequisites

To run the NATS transport tests, you need a running NATS server with JetStream enabled.

### Using Docker Compose

From the repository root:

```bash
docker compose up nats -d
```

Or start NATS directly:

```bash
docker run -d --name nats -p 4222:4222 -p 8222:8222 nats:latest --jetstream -m 8222
```

### Verify NATS is Running

```bash
curl http://localhost:8222/healthz
```

## Running Tests

```bash
dotnet test src/Transports/NATS/Wolverine.Nats.Tests
```

## Configuration

Tests use `nats://localhost:4222` by default. Override with the `NATS_URL` environment variable:

```bash
NATS_URL=nats://my-nats-server:4222 dotnet test src/Transports/NATS/Wolverine.Nats.Tests
```

## Test Categories

- **Integration Tests**: Basic send/receive, request/reply, JetStream functionality
- **Compliance Tests**: Wolverine transport contract compliance (inline, buffered, JetStream modes)
- **Multi-tenancy Tests**: Subject-based tenant isolation (requires specific setup)
