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
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Integration tests for Wolverine message bus operations within gRPC endpoints.
/// Tests InvokeAsync, PublishAsync, SendAsync, and other bus operations.
/// Mirrors patterns from Wolverine.Http.Tests/publishing_messages_from_http_endpoint.cs
/// and sending_messages_from_http_endpoint.cs. 
/// </summary>
public class message_bus_integration : IAsyncLifetime
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
            opts.ApplicationAssembly = typeof(message_bus_integration).Assembly;
            // Don't disable handler discovery - we need it for message bus integration tests
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
    public async Task grpc_endpoint_can_invoke_wolverine_handler()
    {
        var client = _channel!.CreateGrpcService<IBusInvokeContract>();

        var response = await client.InvokeHandlerAsync(
            new BusInvokeRequest { Value = 42 },
            CallContext.Default);

        response.ShouldNotBeNull();
        response.Result.ShouldBe(84); // Handler doubles the value
    }

    [Fact]
    public async Task grpc_endpoint_can_publish_message()
    {
        var client = _channel!.CreateGrpcService<IBusPublishContract>();
        var response = await client.PublishMessageAsync(
            new BusPublishRequest { Topic = "test-topic" },
            CallContext.Default);

        response.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task grpc_endpoint_can_send_message_to_endpoint()
    {
        var client = _channel!.CreateGrpcService<IBusSendContract>();
        var response = await client.SendMessageAsync(
            new BusSendRequest { Destination = "local" },
            CallContext.Default);

        response.Success.ShouldBeTrue();
    }
}

// Test fixtures for message bus integration tests

[ProtoContract]
public class BusInvokeRequest
{
    [ProtoMember(1)]
    public int Value { get; set; }
}

[ProtoContract]
public class BusInvokeResponse
{
    [ProtoMember(1)]
    public int Result { get; set; }
}

[ProtoContract]
public class BusPublishRequest
{
    [ProtoMember(1)]
    public string Topic { get; set; } = "";
}

[ProtoContract]
public class BusPublishResponse
{
    [ProtoMember(1)]
    public bool Success { get; set; }
}

[ProtoContract]
public class BusSendRequest
{
    [ProtoMember(1)]
    public string Destination { get; set; } = "";
}

[ProtoContract]
public class BusSendResponse
{
    [ProtoMember(1)]
    public bool Success { get; set; }
}

// Message types for tracking
public record BusPublishedMessage(string Topic);
public record BusSentMessage(string Destination);
public record HandlerCommand(int Value);
public record HandlerResponse(int Result);

// Service contracts
[ServiceContract]
public interface IBusInvokeContract
{
    [OperationContract]
    Task<BusInvokeResponse> InvokeHandlerAsync(BusInvokeRequest request, CallContext context = default);
}

[ServiceContract]
public interface IBusPublishContract
{
    [OperationContract]
    Task<BusPublishResponse> PublishMessageAsync(BusPublishRequest request, CallContext context = default);
}

[ServiceContract]
public interface IBusSendContract
{
    [OperationContract]
    Task<BusSendResponse> SendMessageAsync(BusSendRequest request, CallContext context = default);
}

// Service implementations
[WolverineGrpcService]
public class BusInvokeService : WolverineGrpcEndpointBase, IBusInvokeContract
{
    public async Task<BusInvokeResponse> InvokeHandlerAsync(BusInvokeRequest request, CallContext context = default)
    {
        var response = await Bus.InvokeAsync<HandlerResponse>(
            new HandlerCommand(request.Value),
            context.CancellationToken);

        return new BusInvokeResponse { Result = response.Result };
    }
}

[WolverineGrpcService]
public class BusPublishService : WolverineGrpcEndpointBase, IBusPublishContract
{
    public async Task<BusPublishResponse> PublishMessageAsync(BusPublishRequest request, CallContext context = default)
    {
        await Bus.PublishAsync(new BusPublishedMessage(request.Topic));
        return new BusPublishResponse { Success = true };
    }
}

[WolverineGrpcService]
public class BusSendService : WolverineGrpcEndpointBase, IBusSendContract
{
    public async Task<BusSendResponse> SendMessageAsync(BusSendRequest request, CallContext context = default)
    {
        await Bus.SendAsync(new BusSentMessage(request.Destination));
        return new BusSendResponse { Success = true };
    }
}

// Wolverine handler for InvokeAsync test
public static class HandlerCommandHandler
{
    public static HandlerResponse Handle(HandlerCommand command)
    {
        return new HandlerResponse(command.Value * 2);
    }
}
