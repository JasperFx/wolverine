#region sample_grpc_ponger_bootstrapping

using JasperFx;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Wolverine is required for WolverineFx.Http.Grpc
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Register Wolverine gRPC services (adds code-first gRPC server support)
builder.Services.AddWolverineGrpc();

var app = builder.Build();

// gRPC requires HTTP/2 – ensure the Kestrel listener is configured accordingly
// in appsettings.json or via environment variables (Kestrel:Endpoints:Grpc:Protocols = Http2)
app.UseRouting();

// Discover and map all Wolverine gRPC endpoint types (e.g., PongerGrpcEndpoint)
app.MapWolverineGrpcEndpoints();

return await app.RunJasperFxCommands(args);

#endregion
