using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using Shouldly;
using System.ServiceModel;
using Wolverine.Http.Grpc;
using Wolverine.Runtime;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// End-to-end integration tests that make actual gRPC client calls over the network
/// (or in-memory channel) to verify the full request/response pipeline works correctly.
/// Mirrors the pattern used in Wolverine.Http.Tests/end_to_end.cs.
/// </summary>
public class end_to_end_grpc_integration : IAsyncLifetime
{
    private WebApplication? _app;
    private GrpcChannel? _channel;
    private string? _serverAddress;

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
            opts.ApplicationAssembly = typeof(end_to_end_grpc_integration).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
        });

        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapWolverineGrpcEndpoints();

        await _app.StartAsync();

        // Kestrel binds to a random port; extract the actual address
        _serverAddress = _app.Urls.First();
        _channel = GrpcChannel.ForAddress(_serverAddress);
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
    public async Task call_grpc_endpoint_with_property_injection_returns_expected_response()
    {
        var client = _channel!.CreateGrpcService<IE2EPropertyInjectionContract>();

        var response = await client.ProcessAsync(new E2ERequest { Message = "test" }, CallContext.Default);

        response.ShouldNotBeNull();
        response.Reply.ShouldBe("Processed: test");
    }

    [Fact]
    public async Task call_grpc_endpoint_with_constructor_injection_returns_expected_response()
    {
        var client = _channel!.CreateGrpcService<IE2EConstructorInjectionContract>();

        var response = await client.ExecuteAsync(new E2ERequest { Message = "ctor test" }, CallContext.Default);

        response.ShouldNotBeNull();
        response.Reply.ShouldBe("Executed: ctor test");
    }

    [Fact]
    public async Task multiple_concurrent_grpc_calls_succeed()
    {
        var client = _channel!.CreateGrpcService<IE2EPropertyInjectionContract>();

        var tasks = Enumerable.Range(1, 10).Select(i =>
            client.ProcessAsync(new E2ERequest { Message = $"msg{i}" }, CallContext.Default));

        var responses = await Task.WhenAll(tasks);

        responses.Length.ShouldBe(10);
        responses.All(r => r.Reply.StartsWith("Processed:")).ShouldBeTrue();
    }
}

// Test fixtures for end-to-end integration tests

[ProtoContract]
public class E2ERequest
{
    [ProtoMember(1)]
    public string Message { get; set; } = "";
}

[ProtoContract]
public class E2EResponse
{
    [ProtoMember(1)]
    public string Reply { get; set; } = "";
}

[ServiceContract]
public interface IE2EPropertyInjectionContract
{
    [OperationContract]
    Task<E2EResponse> ProcessAsync(E2ERequest request, CallContext context = default);
}

[ServiceContract]
public interface IE2EConstructorInjectionContract
{
    [OperationContract]
    Task<E2EResponse> ExecuteAsync(E2ERequest request, CallContext context = default);
}

[WolverineGrpcService]
public class E2EPropertyInjectionService : WolverineGrpcEndpointBase, IE2EPropertyInjectionContract
{
    public Task<E2EResponse> ProcessAsync(E2ERequest request, CallContext context = default)
    {
        return Task.FromResult(new E2EResponse { Reply = $"Processed: {request.Message}" });
    }
}

[WolverineGrpcService]
public class E2EConstructorInjectionService : IE2EConstructorInjectionContract
{
    private readonly IMessageBus _bus;

    public E2EConstructorInjectionService(IMessageBus bus) => _bus = bus;

    public Task<E2EResponse> ExecuteAsync(E2ERequest request, CallContext context = default)
    {
        return Task.FromResult(new E2EResponse { Reply = $"Executed: {request.Message}" });
    }
}
