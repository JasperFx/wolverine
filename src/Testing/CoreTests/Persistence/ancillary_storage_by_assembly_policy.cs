using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Persistence;

// GH-4477. UseAncillaryStorageFromAssembly is the assembly-wide alternative to marking every type in a
// module with [Storage(typeof(IMyStore))], which is what modular monoliths were reduced to doing. These
// tests pin the routing decision itself -- which chains get AncillaryStoreType set and which are left
// alone -- against a stub frame provider, so they need no database and run against Wolverine core only.
//
// The end-to-end "the work actually commits through that store's session" half is covered per provider,
// e.g. MartenTests/AncillaryStores/ancillary_storage_by_assembly.cs.

public interface IByAssemblyStore;

public interface IOtherByAssemblyStore;

public record ByAssemblyMessage(string Name);

public record ByAssemblyAnnotatedMessage(string Name);

public record ByAssemblyUnrelatedMessage(string Name);

public class ByAssemblyHandler
{
    public static void Handle(ByAssemblyMessage message)
    {
    }
}

// An explicit attribute has to beat the assembly-wide default, so a single handler can opt out of
// its module's store.
public class ByAssemblyAnnotatedHandler
{
    [Storage(typeof(IOtherByAssemblyStore))]
    public static void Handle(ByAssemblyAnnotatedMessage message)
    {
    }
}

/// <summary>
/// Stands in for the Marten/Polecat/Fisher frame providers. The policy resolves one of these out of the
/// codegen container and refuses to route without it, so a core-only test has to supply one.
/// </summary>
internal class StubAncillaryStoreFrameProvider : IAncillaryStoreFrameProvider
{
    public bool Matches(Type storeType)
    {
        return storeType == typeof(IByAssemblyStore) || storeType == typeof(IOtherByAssemblyStore);
    }

    public Frame BuildOutboxFactoryFrame(Type storeType)
    {
        return new CommentFrame($"Stub outbox factory for {storeType.Name}");
    }
}

public class ancillary_storage_by_assembly_policy
{
    private static Task<IHost> hostFor(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAncillaryStoreFrameProvider, StubAncillaryStoreFrameProvider>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ByAssemblyHandler>()
                    .IncludeType<ByAssemblyAnnotatedHandler>();

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// ChainFor rather than HandlerFor, deliberately: HandlerFor triggers codegen, which applies
    /// ModifyChainAttribute and inserts the attribute's own outbox frame. Reading the chain without
    /// compiling it is what makes the middleware assertions below about the POLICY.
    /// </summary>
    private static HandlerChain chainFor<T>(IHost host)
    {
        return host.Services.GetRequiredService<HandlerGraph>().ChainFor<T>()!;
    }

    [Fact]
    public async Task routes_a_handler_in_the_named_assembly()
    {
        using var host = await hostFor(opts =>
            opts.Policies.UseAncillaryStorageFromAssemblyContaining<ByAssemblyHandler>(typeof(IByAssemblyStore)));

        chainFor<ByAssemblyMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
    }

    /// <summary>
    /// The policy has to run early enough for <c>WolverineRuntime.HostService</c> to see the assignment
    /// when it builds the message-type-to-store inbox routing map -- that map is read at startup, long
    /// before a chain is compiled. Asserting on a STARTED host is what makes this meaningful: a policy
    /// that ran too late would leave the property null here.
    /// </summary>
    [Fact]
    public async Task the_assignment_is_visible_on_a_started_host()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IAncillaryStoreFrameProvider, StubAncillaryStoreFrameProvider>();
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ByAssemblyHandler>();
                opts.Policies.UseAncillaryStorageFromAssemblyContaining<ByAssemblyHandler>(typeof(IByAssemblyStore));
            }).StartAsync(TestContext.Current.CancellationToken);

        var chain = host.Services.GetRequiredService<HandlerGraph>()
            .HandlerFor<ByAssemblyMessage>()!.As<MessageHandler>().Chain!;

        chain.AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
    }

    [Fact]
    public async Task an_explicit_attribute_beats_the_assembly_default()
    {
        using var host = await hostFor(opts =>
            opts.Policies.UseAncillaryStorageFromAssemblyContaining<ByAssemblyHandler>(typeof(IByAssemblyStore)));

        chainFor<ByAssemblyAnnotatedMessage>(host).AncillaryStoreType
            .ShouldBe(typeof(IOtherByAssemblyStore),
                "An explicit [Storage] on the handler must win over its module's assembly-wide default.");
    }

    /// <summary>
    /// Not just a precedence nicety. <c>StorageAttribute.Modify</c> runs at codegen, after policies, and
    /// inserts its own outbox factory frame -- so a policy that routed the annotated chain anyway would
    /// leave it carrying two. The policy must insert nothing there at all.
    /// </summary>
    [Fact]
    public async Task the_policy_inserts_no_frame_on_an_annotated_chain()
    {
        using var host = await hostFor(opts =>
            opts.Policies.UseAncillaryStorageFromAssemblyContaining<ByAssemblyHandler>(typeof(IByAssemblyStore)));

        var routed = chainFor<ByAssemblyMessage>(host);
        routed.Middleware.OfType<CommentFrame>().Count()
            .ShouldBe(1, "The policy routed this chain, so it inserted the outbox factory frame.");

        var annotated = chainFor<ByAssemblyAnnotatedMessage>(host);
        annotated.Middleware.OfType<CommentFrame>().Count()
            .ShouldBe(0, "The attribute inserts its own frame later at codegen; the policy must not add a second.");
    }

    [Fact]
    public async Task leaves_handlers_from_another_assembly_alone()
    {
        // WolverineOptions lives in the Wolverine assembly, so naming it scopes the policy to an
        // assembly that holds none of these handlers.
        using var host = await hostFor(opts =>
            opts.Policies.UseAncillaryStorageFromAssemblyContaining<WolverineOptions>(typeof(IByAssemblyStore)));

        chainFor<ByAssemblyMessage>(host).AncillaryStoreType.ShouldBeNull();
    }

    [Fact]
    public async Task the_assembly_overload_takes_an_assembly_directly()
    {
        using var host = await hostFor(opts =>
            opts.Policies.UseAncillaryStorageFromAssembly(typeof(IByAssemblyStore),
                typeof(ByAssemblyHandler).Assembly));

        chainFor<ByAssemblyMessage>(host).AncillaryStoreType.ShouldBe(typeof(IByAssemblyStore));
    }
}
