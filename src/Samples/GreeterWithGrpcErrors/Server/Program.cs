using FluentValidation;
using GreeterWithGrpcErrors.Messages;
using GreeterWithGrpcErrors.Server;
using Google.Rpc;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProtoBuf.Grpc.Server;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.FluentValidation.Grpc;
using Wolverine.Grpc;

var builder = WebApplication.CreateBuilder(args);

// gRPC runs over HTTP/2. The local sample listens on unencrypted HTTP/2 so
// no trusted dev cert is needed.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5005, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Host.UseWolverine(opts =>
{
    opts.ApplicationAssembly = typeof(Program).Assembly;

    // FluentValidation middleware — any ValidationException thrown out of a handler
    // bubbles up to the gRPC interceptor.
    opts.UseFluentValidation();

    // Opt in to google.rpc.Status + grpc-status-details-bin trailer. The MapException
    // call below adds a rich payload for our domain exception; the validation path is
    // wired by UseFluentValidationGrpcErrorDetails() immediately after.
    opts.UseGrpcRichErrorDetails(cfg =>
    {
        cfg.MapException<GreetingForbiddenException>(
            StatusCode.FailedPrecondition,
            (ex, _) => new[]
            {
                new PreconditionFailure
                {
                    Violations =
                    {
                        new PreconditionFailure.Types.Violation
                        {
                            Type = "policy.banned_name",
                            Subject = ex.Subject,
                            Description = ex.Reason
                        }
                    }
                }
            });
    });

    // Bridges FluentValidation.ValidationException → google.rpc.BadRequest with one
    // FieldViolation per failure. The gRPC counterpart to ValidationProblemDetails.
    opts.UseFluentValidationGrpcErrorDetails();
});

// The GreetRequestValidator lives in the Messages project so both server and client
// can see the contract; Wolverine.FluentValidation's scanner only looks inside the
// host's application assembly, so register the cross-assembly validator explicitly.
builder.Services.AddScoped<IValidator<GreetRequest>, GreetRequestValidator>();

builder.Services.AddCodeFirstGrpc();
builder.Services.AddWolverineGrpc();

var app = builder.Build();
app.UseRouting();

// Discovers GreeterGrpcService by the 'GrpcService' suffix convention.
app.MapWolverineGrpcServices();

app.Run();

public partial class Program;
