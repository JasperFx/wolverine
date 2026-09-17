using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-4477. [Storage(typeof(IMyStore))] is silently ignored on a gRPC service: the three gRPC chains
// derive from Chain<,> but none of them calls applyAttributesAndConfigureMethods, so no
// ModifyChainAttribute is ever applied there. That left a modular monolith with no way at all to point
// a gRPC service at its module's ancillary store.
//
// UseAncillaryStorageFromAssembly is an IChainPolicy, and GrpcGraph.DiscoverServices DOES apply
// IChainPolicy off WolverineOptions.Policies -- so the policy reaches gRPC where the attribute cannot.
// This pins that, and the empty-HandlerCalls() trap underneath it: all three gRPC chains return an
// empty array from HandlerCalls(), so a policy that looked only there would no-op over every gRPC
// service while appearing to work.

public interface IGrpcByAssemblyStore;

internal class GrpcStubAncillaryStoreFrameProvider : IAncillaryStoreFrameProvider
{
    public bool Matches(Type storeType) => storeType == typeof(IGrpcByAssemblyStore);

    public Frame BuildOutboxFactoryFrame(Type storeType)
    {
        return new CommentFrame($"Stub outbox factory for {storeType.Name}");
    }
}

public class ancillary_storage_by_assembly_reaches_grpc_4477 : IAsyncLifetime
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

            // The gRPC services under test live in the PingGrpcService assembly, so that is the
            // "module" this policy is scoped to.
            opts.Policies.UseAncillaryStorageFromAssemblyContaining<PingGrpcService>(
                typeof(IGrpcByAssemblyStore));
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

    private GrpcGraph theGraph => _app.Services.GetRequiredService<GrpcGraph>();

    /// <summary>
    /// The three gRPC chain kinds share no ancestor of their own, so this walks all three lists rather
    /// than trusting that whichever one the sample app happens to produce is representative.
    /// </summary>
    private IEnumerable<IGrpcChain> allGrpcChains()
    {
        foreach (var chain in theGraph.Chains) yield return chain;
        foreach (var chain in theGraph.CodeFirstChains) yield return chain;
        foreach (var chain in theGraph.HandWrittenChains) yield return chain;
    }

    [Fact]
    public void the_sample_app_actually_produced_grpc_chains()
    {
        // Guards the tests below against passing vacuously over an empty list.
        allGrpcChains().ShouldNotBeEmpty();
    }

    [Fact]
    public void every_grpc_chain_in_the_assembly_is_routed_to_the_ancillary_store()
    {
        foreach (var chain in allGrpcChains())
        {
            chain.AncillaryStoreType.ShouldBe(typeof(IGrpcByAssemblyStore),
                $"{chain.ServiceType.Name} is in the policy's assembly and should have been routed.");
        }
    }

    /// <summary>
    /// The reason the policy cannot be written against HandlerCalls() alone. If this ever starts
    /// returning something, the policy's IChainSourceType branch is no longer the only thing keeping
    /// gRPC covered -- but until then, it is.
    /// </summary>
    [Fact]
    public void grpc_chains_report_no_handler_calls()
    {
        foreach (var chain in allGrpcChains())
        {
            chain.HandlerCalls().ShouldBeEmpty();
        }
    }

    [Fact]
    public void grpc_chains_expose_their_source_type_to_core()
    {
        foreach (var chain in allGrpcChains())
        {
            chain.ShouldBeAssignableTo<IChainSourceType>();
            ((IChainSourceType)chain).SourceType.ShouldBe(chain.ServiceType);
        }
    }
}
