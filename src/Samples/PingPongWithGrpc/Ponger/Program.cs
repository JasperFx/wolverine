using Microsoft.AspNetCore.Server.Kestrel.Core;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC runs over HTTP/2. Kestrel uses HTTP/1.1 + HTTP/2 on HTTPS by default,
// but for a local sample it's simpler to listen for unencrypted HTTP/2 so the
// client doesn't need a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5001, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Code-first gRPC host (protobuf-net.Grpc) + Wolverine's gRPC adapter.
builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

// Singleton counter so the sample can demonstrate that the Wolverine handler
// (not just the gRPC service method) executes on every call.
builder.Services.AddSingleton<PingTracker>();

var app = builder.Build();
app.UseRouting();

// Discovers PingGrpcService by convention (name ends in 'GrpcService').
app.MapWolverineGrpcServices();

app.Run();

public partial class Program;
