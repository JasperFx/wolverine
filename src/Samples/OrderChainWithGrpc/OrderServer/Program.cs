using JasperFx;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OrderChainWithGrpc.Contracts;
using OrderChainWithGrpc.OrderServer;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.Grpc;
using Wolverine.Grpc.Client;

var builder = WebApplication.CreateBuilder(args);

// gRPC requires HTTP/2. Plain-HTTP/2 listener keeps the sample runnable without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5006, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    // Distinct ServiceName so chained log lines are easy to tell apart from the downstream.
    opts.ServiceName = "OrderServer";
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

#region sample_order_chain_add_wolverine_grpc_client
// The one new registration line compared to a normal Wolverine gRPC server. Wolverine resolves
// IInventoryService into any handler (like PlaceOrderHandler) that asks for it, routes the call
// through the typed gRPC client, and stamps envelope headers automatically. No GrpcChannel, no
// Metadata wiring, no custom interceptors.
builder.Services.AddWolverineGrpcClient<IInventoryService>(o =>
{
    o.Address = new Uri("http://localhost:5007");
});
#endregion

var app = builder.Build();
app.UseRouting();

// Discovers OrderGrpcService by convention (name ends in 'GrpcService').
app.MapWolverineGrpcServices();

return await app.RunJasperFxCommands(args);

public partial class Program;
