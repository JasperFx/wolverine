using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-4383: Wolverine generates the type that gets mapped, so an [Authorize] on a hand-written service
// class is never seen by the router and the only place a policy was picked up was the
// [ServiceContract] interface -- which put Microsoft.AspNetCore.Authorization, and the policy-name
// constants with it, on the wire-contract assembly that clients also reference. MapGeneratedServices
// discarded the GrpcServiceEndpointConventionBuilder that MapGrpcService<T> hands back, so there was
// no seam anywhere. Now there is.
public class grpc_endpoint_conventions_4383 : IAsyncLifetime
{
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;
        });

        builder.Services.AddGrpc();
        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("scanner", policy => policy.RequireAssertion(_ => true));
        });

        builder.Services.AddWolverineGrpc(opts =>
        {
            opts.RequireAuthorizeOnAll("scanner");
            opts.ConfigureEndpoints(chain =>
                chain.WithMetadata(new ChainMarker(chain.ServiceType, chain.GetType().Name)));
        });

        builder.Services.AddSingleton<PingTracker>();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapWolverineGrpcServices();

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private IReadOnlyList<Endpoint> grpcEndpoints()
        => _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(x => x.Metadata.GetMetadata<GrpcMethodMetadata>() != null)
            .ToList();

    [Fact]
    public void the_generated_endpoints_are_actually_mapped()
    {
        // guards the rest of this fixture from going vacuously green on an empty set
        grpcEndpoints().ShouldNotBeEmpty();
    }

    [Fact]
    public void require_authorize_on_all_puts_the_policy_on_the_endpoint()
    {
        foreach (var endpoint in grpcEndpoints())
        {
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            authorize.ShouldNotBeEmpty();
            authorize.ShouldContain(x => x.Policy == "scanner");
        }
    }

    [Fact]
    public void configure_endpoints_reaches_every_wolverine_managed_chain_kind()
    {
        var markers = grpcEndpoints()
            .Select(x => x.Metadata.GetMetadata<ChainMarker>())
            .Where(x => x != null)
            .ToList();

        markers.ShouldNotBeEmpty();

        // the chain knows the type it was built from, so a convention can target one service
        markers.ShouldAllBe(x => x!.ServiceType != null);
    }

    [Fact]
    public void a_convention_can_target_a_single_rpc_through_its_grpc_method_metadata()
    {
        // per-RPC targeting needs no new API: every gRPC endpoint already carries GrpcMethodMetadata,
        // so a convention matches on Method.Name rather than on a route string
        var endpoint = grpcEndpoints().First();
        endpoint.Metadata.GetMetadata<GrpcMethodMetadata>()!.Method.Name.ShouldNotBeNullOrEmpty();
    }
}

internal sealed record ChainMarker(Type ServiceType, string ChainKind);
