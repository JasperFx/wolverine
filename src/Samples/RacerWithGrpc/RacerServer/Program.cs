#region sample_racer_server_bootstrapping

using JasperFx;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProtoBuf.Grpc.Server;
using RacerServer;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC requires HTTP/2. Listen on an unencrypted HTTP/2 endpoint so the sample
// runs without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5004, listen => listen.Protocols = HttpProtocols.Http2);
});

// Singleton race state to track the current speed of each racer across updates.
builder.Services.AddSingleton<RaceState>();

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Code-first gRPC host + Wolverine's gRPC adapter.
builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();

// Discovers RacingGrpcService by convention (and its [WolverineGrpcService] marker).
app.MapWolverineGrpcServices();

return await app.RunJasperFxCommands(args);

#endregion

public partial class Program;
