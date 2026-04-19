# OrderChainWithGrpc

Two-hop Wolverine gRPC chain demonstrating the typed client extension
(`AddWolverineGrpcClient<T>()`), envelope propagation, and typed-exception
round-tripping:

```
OrderClient  ->  OrderServer  ->  InventoryServer
              (Wolverine)      (Wolverine)
```

The `OrderServer` handler depends on `IInventoryService` and calls it like any
other collaborator — no `GrpcChannel`, `Metadata`, or `CallOptions` wiring.
The typed client stamps envelope headers on the outbound call; the downstream
unpacks them back into its own `IMessageContext`; typed exceptions are
translated back to their original .NET types at the call site.

- `Contracts` — `IOrderService`, `IInventoryService`, DTOs.
- `InventoryServer` — downstream; `ServiceName = "InventoryServer"`, port 5007.
- `OrderServer` — upstream; registers `IInventoryService` as a Wolverine gRPC client pointed at `http://localhost:5007`; port 5006.
- `OrderClient` — vanilla grpc-dotnet caller (deliberately not a Wolverine client — it represents an arbitrary external caller).

## Running

Three terminals. Start the downstream first:

```sh
dotnet run --project src/Samples/OrderChainWithGrpc/InventoryServer --framework net9.0
```

```sh
dotnet run --project src/Samples/OrderChainWithGrpc/OrderServer --framework net9.0
```

```sh
dotnet run --project src/Samples/OrderChainWithGrpc/OrderClient --framework net9.0
```

Expected output on the client:

```
OrderChainWithGrpc — external caller -> OrderServer -> InventoryServer
=====================================================================

-> PlaceOrder(Sku=ABC-123, Quantity=2)
   Reservation:               2a1dcd1266e0478899c01066779efe5e
   Correlation-id, both hops: c12a4848de913c909840ac68be136959

-> PlaceOrder(Sku=UNKNOWN, Quantity=1)  (expected: NotFound)
   NotFound from OrderServer: SKU 'UNKNOWN' is not stocked
```

- `Correlation-id, both hops` is non-empty because `InventoryServer` read it
  from its own `IMessageContext.CorrelationId` and echoed it back — the value
  was put there by the propagation interceptor, not by any user code.
- The `NotFound` line is the full exception round-trip: downstream throws
  `KeyNotFoundException`, the wire carries `NotFound`, `OrderServer`'s client
  interceptor rehydrates it as `KeyNotFoundException` at the call site, and
  `OrderServer`'s server interceptor re-maps that to `NotFound` for the
  external caller.
