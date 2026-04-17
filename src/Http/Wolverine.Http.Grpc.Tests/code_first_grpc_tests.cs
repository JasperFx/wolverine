using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Grpc.Tests;

[Collection("grpc")]
public class code_first_grpc_tests : IClassFixture<GrpcTestFixture>
{
    private readonly GrpcTestFixture _fixture;

    public code_first_grpc_tests(GrpcTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task round_trip_unary_call_through_wolverine_handler()
    {
        var client = _fixture.CreateClient<IPingService>();

        var reply = await client.Ping(new PingRequest { Message = "hello" });

        reply.Echo.ShouldBe("hello");
        reply.HandledCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task cancellation_propagates_to_wolverine_handler()
    {
        var client = _fixture.CreateClient<IPingService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<Exception>(async () =>
            await client.Ping(new PingRequest { Message = "cancelled" }, cts.Token));
    }

    [Fact]
    public async Task round_trip_server_streaming_call_through_wolverine_handler()
    {
        var client = _fixture.CreateClient<IPingStreamService>();

        var replies = new List<PongReply>();
        await foreach (var reply in client.PingStream(new PingStreamRequest { Message = "stream", Count = 3 }))
        {
            replies.Add(reply);
        }

        replies.Count.ShouldBe(3);
        replies[0].Echo.ShouldBe("stream:0");
        replies[1].Echo.ShouldBe("stream:1");
        replies[2].Echo.ShouldBe("stream:2");
    }

    [Fact]
    public async Task mid_stream_cancellation_stops_enumeration_early()
    {
        var client = _fixture.CreateClient<IPingStreamService>();
        using var cts = new CancellationTokenSource();
        var ctx = new CallContext(new CallOptions(cancellationToken: cts.Token));

        var received = 0;
        await Should.ThrowAsync<Exception>(async () =>
        {
            await foreach (var _ in client.PingStream(new PingStreamRequest { Message = "cancel", Count = 1000 }, ctx))
            {
                received++;
                if (received == 2)
                {
                    cts.Cancel();
                }
            }
        });

        // We must have stopped well before the handler produced all 1000 items.
        received.ShouldBeLessThan(1000);
    }
}

[Collection("grpc-discovery")]
public class convention_discovery_tests
{
    [Fact]
    public void finds_grpc_service_suffix_types()
    {
        var assemblies = new[] { typeof(PingGrpcService).Assembly };
        var types = WolverineGrpcExtensions.FindGrpcServiceTypes(assemblies).ToList();

        types.ShouldContain(typeof(PingGrpcService));
    }

    [Fact]
    public void finds_wolverine_grpc_service_attribute_types()
    {
        var assemblies = new[] { typeof(AttributeMarkedService).Assembly };
        var types = WolverineGrpcExtensions.FindGrpcServiceTypes(assemblies).ToList();

        types.ShouldContain(typeof(AttributeMarkedService));
    }

    [Fact]
    public void does_not_discover_abstract_types()
    {
        var assemblies = new[] { typeof(WolverineGrpcServiceBase).Assembly };
        var types = WolverineGrpcExtensions.FindGrpcServiceTypes(assemblies).ToList();

        types.ShouldNotContain(typeof(WolverineGrpcServiceBase));
    }

    [Fact]
    public async Task map_wolverine_grpc_services_discovers_and_maps_grpc_service_types()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        var app = builder.Build();
        app.UseRouting();

        // This should discover PingGrpcService via the "GrpcService" suffix convention
        app.MapWolverineGrpcServices();

        await app.StartAsync();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>
/// A type that should be discovered via [WolverineGrpcService] even though
/// its name does not end with "GrpcService".
/// </summary>
[WolverineGrpcService]
public class AttributeMarkedService
{
}
