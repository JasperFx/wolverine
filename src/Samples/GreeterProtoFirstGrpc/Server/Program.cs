using Microsoft.AspNetCore.Server.Kestrel.Core;
using Wolverine;
using Wolverine.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC runs over HTTP/2. Listen on an unencrypted HTTP/2 endpoint so the
// sample runs without a trusted dev cert.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5003, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;
});

// Proto-first uses the stock ASP.NET Core gRPC host (not the code-first one).
builder.Services.AddGrpc();
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();

// Discovers the abstract [WolverineGrpcService] stub and maps the generated
// concrete wrapper (GreeterGrpcHandler) that forwards each RPC to IMessageBus.
app.MapWolverineGrpcServices();

app.Run();

public partial class Program;
