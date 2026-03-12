using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using Shouldly;
using System.ServiceModel;
using Wolverine.Http.Grpc;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Tests for error handling, exceptions, and edge cases in gRPC endpoints.
/// Verifies proper exception propagation, cancellation handling, and error responses.
/// Mirrors patterns from Wolverine.Http.Tests for error scenarios.
/// </summary>
public class error_handling_and_exceptions : IAsyncLifetime
{
    private WebApplication? _app;
    private GrpcChannel? _channel;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // Configure Kestrel to use HTTP/2 (required for gRPC)
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(error_handling_and_exceptions).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
        });

        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapWolverineGrpcEndpoints();

        await _app.StartAsync();

        var serverAddress = _app.Urls.First();
        _channel = GrpcChannel.ForAddress(serverAddress);
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task exception_in_grpc_endpoint_propagates_as_rpc_exception()
    {
        var client = _channel!.CreateGrpcService<IErrorHandlingContract>();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
        {
            await client.ThrowExceptionAsync(
                new ErrorRequest { ShouldFail = true },
                CallContext.Default);
        });

        ex.StatusCode.ShouldBe(StatusCode.Unknown);
        ex.Status.Detail.ShouldContain("Intentional test exception");
    }

    [Fact]
    public async Task argument_null_exception_propagates_correctly()
    {
        var client = _channel!.CreateGrpcService<IErrorHandlingContract>();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
        {
            await client.ValidateArgumentAsync(
                new ErrorRequest { Value = null },
                CallContext.Default);
        });

        ex.StatusCode.ShouldBe(StatusCode.Unknown);
    }

    [Fact]
    public async Task grpc_endpoint_handles_empty_request_gracefully()
    {
        var client = _channel!.CreateGrpcService<IErrorHandlingContract>();

        var response = await client.HandleEmptyRequestAsync(
            new ErrorRequest(),
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Message.ShouldBe("Handled empty request");
    }

    [Fact]
    public async Task grpc_endpoint_handles_null_properties_in_request()
    {
        var client = _channel!.CreateGrpcService<IErrorHandlingContract>();

        var response = await client.HandleNullPropertiesAsync(
            new ErrorRequest { Value = null, Message = null },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Message.ShouldContain("null");
    }

    [Fact]
    public async Task concurrent_requests_with_failures_dont_affect_each_other()
    {
        var client = _channel!.CreateGrpcService<IErrorHandlingContract>();

        var tasks = new List<Task<ErrorResponse>>();

        for (int i = 0; i < 10; i++)
        {
            if (i % 2 == 0)
            {
                tasks.Add(client.HandleEmptyRequestAsync(
                    new ErrorRequest(),
                    CallContext.Default));
            }
            else
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        return await client.ThrowExceptionAsync(
                            new ErrorRequest { ShouldFail = true },
                            CallContext.Default);
                    }
                    catch (RpcException)
                    {
                        return new ErrorResponse { Message = "Failed" };
                    }
                }));
            }
        }

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Message.Contains("Handled")).ShouldBeGreaterThan(0);
    }
}

// Test fixtures for error handling tests

[ProtoContract]
public class ErrorRequest
{
    [ProtoMember(1)]
    public bool ShouldFail { get; set; }

    [ProtoMember(2)]
    public string? Value { get; set; }

    [ProtoMember(3)]
    public string? Message { get; set; }
}

[ProtoContract]
public class ErrorResponse
{
    [ProtoMember(1)]
    public string Message { get; set; } = "";
}

[ServiceContract]
public interface IErrorHandlingContract
{
    [OperationContract]
    Task<ErrorResponse> ThrowExceptionAsync(ErrorRequest request, CallContext context = default);

    [OperationContract]
    Task<ErrorResponse> ValidateArgumentAsync(ErrorRequest request, CallContext context = default);

    [OperationContract]
    Task<ErrorResponse> HandleEmptyRequestAsync(ErrorRequest request, CallContext context = default);

    [OperationContract]
    Task<ErrorResponse> HandleNullPropertiesAsync(ErrorRequest request, CallContext context = default);
}

[WolverineGrpcService]
public class ErrorHandlingService : WolverineGrpcEndpointBase, IErrorHandlingContract
{
    public Task<ErrorResponse> ThrowExceptionAsync(ErrorRequest request, CallContext context = default)
    {
        if (request.ShouldFail)
        {
            throw new InvalidOperationException("Intentional test exception");
        }

        return Task.FromResult(new ErrorResponse { Message = "Success" });
    }

    public Task<ErrorResponse> ValidateArgumentAsync(ErrorRequest request, CallContext context = default)
    {
        ArgumentNullException.ThrowIfNull(request.Value);

        return Task.FromResult(new ErrorResponse { Message = $"Validated: {request.Value}" });
    }

    public Task<ErrorResponse> HandleEmptyRequestAsync(ErrorRequest request, CallContext context = default)
    {
        return Task.FromResult(new ErrorResponse { Message = "Handled empty request" });
    }

    public Task<ErrorResponse> HandleNullPropertiesAsync(ErrorRequest request, CallContext context = default)
    {
        var msg = $"Value: {request.Value ?? "null"}, Message: {request.Message ?? "null"}";
        return Task.FromResult(new ErrorResponse { Message = msg });
    }
}
