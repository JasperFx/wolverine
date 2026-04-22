using GreeterCodeFirstGrpc.Messages;
using JasperFx;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC runs over HTTP/2. Listen on an unencrypted HTTP/2 endpoint so the
// sample runs without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5008, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
    // The [WolverineGrpcService] interface lives in the shared Messages assembly;
    // include it so GrpcGraph can discover IGreeterCodeFirstService at startup.
    opts.Discovery.IncludeAssembly(typeof(IGreeterCodeFirstService).Assembly);
});

// Code-first gRPC requires AddCodeFirstGrpc() (protobuf-net.Grpc) rather than AddGrpc().
// No concrete service class is registered — Wolverine discovers IGreeterCodeFirstService
// (annotated with [WolverineGrpcService]) and generates the implementation at startup.
builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();
app.MapWolverineGrpcServices();

return await app.RunJasperFxCommands(args);

public partial class Program;
