using JasperFx;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProgressTrackerWithGrpc.Messages;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC runs over HTTP/2. Listen on an unencrypted HTTP/2 endpoint so the
// sample runs without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5009, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
    // The [WolverineGrpcService] interface lives in the shared Messages assembly;
    // include it so GrpcGraph can discover IProgressTrackerService at startup.
    opts.Discovery.IncludeAssembly(typeof(IProgressTrackerService).Assembly);
});

// Code-first gRPC: Wolverine discovers IProgressTrackerService (annotated with
// [WolverineGrpcService]) and generates the concrete implementation at startup.
builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();
app.MapWolverineGrpcServices();

return await app.RunJasperFxCommands(args);

public partial class Program;
