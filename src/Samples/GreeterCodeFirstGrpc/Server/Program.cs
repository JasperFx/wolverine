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
    // Handlers live here. The contract assembly does not need to be scanned: the
    // contract is named explicitly below rather than discovered by attribute.
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Code-first gRPC requires AddCodeFirstGrpc() (protobuf-net.Grpc) rather than AddGrpc().
// No concrete service class is registered. IGreeterCodeFirstService carries only
// [ServiceContract] (its assembly never references WolverineFx.Grpc), so the host names it
// here and Wolverine generates and maps the implementation at startup.
builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IGreeterCodeFirstService>());

var app = builder.Build();
app.UseRouting();
app.MapWolverineGrpcServices();

return await app.RunJasperFxCommands(args);

public partial class Program;
