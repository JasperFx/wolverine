using Microsoft.AspNetCore.Server.Kestrel.Core;
using PingPongWithGrpcStreaming.Ponger;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC runs over HTTP/2. Listen on an unencrypted HTTP/2 endpoint so the
// sample runs without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5002, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();
builder.Services.AddSingleton<PingTracker>();

var app = builder.Build();
app.UseRouting();
app.MapWolverineGrpcServices();

app.Run();

public partial class Program;
