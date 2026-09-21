using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Wolverine.Persistence;
using Xunit;

namespace Wolverine.Grpc.Tests;

// The namespace policy matches gRPC chains through IChainSourceType, as the assembly policy does.
// Reuses the stub frame provider from ancillary_storage_by_assembly_reaches_grpc_4477.cs.
public class ancillary_storage_by_namespace_reaches_grpc : IAsyncLifetime
{
    private WebApplication _app = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;

            opts.Services.AddSingleton<IAncillaryStoreFrameProvider, GrpcStubAncillaryStoreFrameProvider>();

            // The parent of both PingPongWithGrpc.Ponger and PingPongWithGrpc.Messages
            opts.Policies.UseAncillaryStorageFromNamespace(typeof(IGrpcByAssemblyStore), "PingPongWithGrpc");
        });

        builder.Services.AddGrpc();
        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();
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

    private IEnumerable<IGrpcChain> allGrpcChains()
    {
        var graph = _app.Services.GetRequiredService<GrpcGraph>();
        foreach (var chain in graph.Chains) yield return chain;
        foreach (var chain in graph.CodeFirstChains) yield return chain;
        foreach (var chain in graph.HandWrittenChains) yield return chain;
    }

    [Fact]
    public void every_grpc_chain_below_the_namespace_is_routed()
    {
        var chains = allGrpcChains().ToArray();
        chains.ShouldNotBeEmpty();

        foreach (var chain in chains)
        {
            chain.AncillaryStoreType.ShouldBe(typeof(IGrpcByAssemblyStore), chain.ServiceType.FullName);
        }
    }
}
