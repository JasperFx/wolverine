#region sample_proto_first_grpc_server_bootstrapping

using JasperFx;
using ProtoFirstServer;
using Wolverine;
using Wolverine.Http.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Wolverine is required for WolverineFx.Http.Grpc
builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Register Wolverine gRPC services.
// Because this is proto-first, AddWolverineGrpc() still calls services.AddGrpc()
// under the covers, which is required by Grpc.AspNetCore.
builder.Services.AddWolverineGrpc();

var app = builder.Build();

app.UseRouting();

// Discover and map all types decorated with [WolverineGrpcService].
// GreeterService is discovered here even though it inherits from the proto-generated
// Greeter.GreeterBase rather than WolverineGrpcEndpointBase, because the
// [WolverineGrpcService] attribute enables attribute-based discovery without
// requiring the base class.
app.MapWolverineGrpcEndpoints();

return await app.RunJasperFxCommands(args);

#endregion
