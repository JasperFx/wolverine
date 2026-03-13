#region sample_racer_server_bootstrapping

using JasperFx;
using RacerServer;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Register singleton race state to track speeds across all racer updates
builder.Services.AddSingleton<RaceState>();

// Wolverine is required for WolverineFx.Http.Grpc
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;

    // Suppress "No routes" logging for streaming responses that aren't meant to be routed
    opts.NoRouteLogging = NoRouteBehavior.Silent;
});

// Register Wolverine gRPC services (adds code-first gRPC server support via protobuf-net.Grpc)
builder.Services.AddWolverineGrpc();

var app = builder.Build();

// gRPC requires HTTP/2 — configure Kestrel endpoints in appsettings.json
// (see Kestrel:Endpoints:Grpc:Protocols = Http2)
app.UseRouting();

// Discover and map RacingGrpcService (found via convention-based discovery)
app.MapWolverineGrpcEndpoints();

return await app.RunJasperFxCommands(args);

#endregion
