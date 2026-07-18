# RacerWithGrpc

Bidirectional-streaming Wolverine gRPC sample. The client sends a continuous
stream of `RacerUpdate` messages (speeds for three racers); the server
computes the current standings on every update and streams back a
`RacePosition` per racer.

Architecturally: the gRPC service consumes the client stream itself and
calls `IMessageBus.StreamAsync<T>(update)` per inbound item, re-yielding
each `RacePosition` the handler produces. Bidi "just works" without a
bidi-shaped primitive on the bus — `IMessageBus.StreamAsync<TRequest, TResponse>`
accepts an inbound stream, but folds it into a single response (client
streaming), so the per-item bridge is still the bidi pattern.

- `RacerContracts` — `[ServiceContract] IRacingService.RaceAsync(IAsyncEnumerable<RacerUpdate>)`.
- `RacerServer` — host with a singleton `RaceState` and `RaceStreamHandler`.
- `RacerClient` — produces round-robin speed updates and prints standings.

## Running

In one terminal:

```sh
dotnet run --project src/Samples/RacerWithGrpc/RacerServer --framework net9.0
```

In another:

```sh
dotnet run --project src/Samples/RacerWithGrpc/RacerClient --framework net9.0
```

Expected output on the client (Ctrl+C to stop):

```
RacerClient connecting to http://localhost:5004
Starting race — press Ctrl+C to stop.

  Racer-A     position=1  speed= 150.5 km/h
  Racer-B     position=1  speed= 196.8 km/h
  Racer-A     position=2  speed= 150.5 km/h
  Racer-B     position=1  speed= 196.8 km/h
  Racer-C     position=2  speed= 157.8 km/h
  ...
```

The server-side console shows the same standings with a `*` marker on whichever racer's update just arrived — useful to watch the round-robin client stream reach the handler.
