#region sample_order_chain_client_kicks_off_chain

using Grpc.Core;
using Grpc.Net.Client;
using OrderChainWithGrpc.Contracts;
using ProtoBuf.Grpc.Client;

// The OrderClient is deliberately a vanilla grpc-dotnet caller — NOT a Wolverine-flavored
// client — because its identity in the sample is "an arbitrary external caller". The
// Wolverine-specific plumbing happens between OrderServer and InventoryServer, not here.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

using var channel = GrpcChannel.ForAddress("http://localhost:5006");
var orders = channel.CreateGrpcService<IOrderService>();

Console.WriteLine("OrderChainWithGrpc — external caller -> OrderServer -> InventoryServer");
Console.WriteLine("=====================================================================");
Console.WriteLine();

// Success path: the correlation-id generated inside OrderServer's IMessageContext should be
// stamped on the outbound call to InventoryServer, read by InventoryServer's
// IMessageContext, and echoed back on the reply.
Console.WriteLine("-> PlaceOrder(Sku=ABC-123, Quantity=2)");
var accepted = await orders.PlaceOrder(new PlaceOrder { Sku = "ABC-123", Quantity = 2 });
Console.WriteLine($"   Reservation:               {accepted.ReservationId}");
Console.WriteLine($"   Correlation-id, both hops: {accepted.CorrelationIdSeenAtBothHops}");
Console.WriteLine();

// Failure path: downstream throws KeyNotFoundException. InventoryServer maps it to NotFound;
// OrderServer's client-side interceptor surfaces it as KeyNotFoundException at the handler's
// call site; OrderServer's server-side interceptor re-maps THAT to NotFound for this external
// caller. End-to-end typed-exception plumbing with zero user code translating between layers.
Console.WriteLine("-> PlaceOrder(Sku=UNKNOWN, Quantity=1)  (expected: NotFound)");
try
{
    await orders.PlaceOrder(new PlaceOrder { Sku = "UNKNOWN", Quantity = 1 });
    Console.WriteLine("   (unexpected: call succeeded)");
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
{
    Console.WriteLine($"   NotFound from OrderServer: {ex.Status.Detail}");
}

#endregion
