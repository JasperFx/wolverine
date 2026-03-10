#region sample_racer_server_bootstrapping

using JasperFx;
using RacerServer;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Wolverine is required for WolverineFx.Http.Grpc
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Register Wolverine gRPC services (adds code-first gRPC server support via protobuf-net.Grpc)
builder.Services.AddWolverineGrpc();

var app = builder.Build();

// gRPC requires HTTP/2 — configure Kestrel endpoints in appsettings.json
// (see Kestrel:Endpoints:Grpc:Protocols = Http2)
app.UseRouting();

// Discover and map RacingService (found via [WolverineGrpcService] attribute)
app.MapWolverineGrpcEndpoints();

return await app.RunJasperFxCommands(args);

#endregion
