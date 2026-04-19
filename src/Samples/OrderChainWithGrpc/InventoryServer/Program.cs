using JasperFx;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OrderChainWithGrpc.InventoryServer;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC requires HTTP/2. Plain-HTTP/2 listener keeps the sample runnable without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5007, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    // Distinct ServiceName so chained log lines are easy to tell apart from the upstream.
    opts.ServiceName = "InventoryServer";
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();

// Discovers InventoryGrpcService by convention (name ends in 'GrpcService').
app.MapWolverineGrpcServices();

return await app.RunJasperFxCommands(args);

public partial class Program;
