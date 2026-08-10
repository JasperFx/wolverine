using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.Bugs;

// Permutation coverage for how a handler chain's ancillary store is resolved for the durable inbox,
// across the ways a chain can be sticky and the ways it can name (or not name) a store. Grew out of
// GH-3886; the bug fixed there was only one cell of this matrix.
//
// These assert the routing DECISION -- what MessageStoreCollection answers for a given (endpoint,
// message type) -- rather than driving messages end to end, so one host covers the whole matrix. The
// end-to-end proof that the decision reaches the database lives in Bug_3886_sticky_handlers_ancillary_store
// (external Rabbit endpoints, DurableReceiver) and sticky_local_queue_ancillary_stores (local queues,
// DurableLocalQueue). Sticky handlers here name endpoints that do not otherwise exist, so Wolverine
// creates local queues for them and no broker is needed.
//
// NOTE for anyone adding a permutation: HandlerChain only splits into per-endpoint ByEndpoint chains
// when a message type has MORE THAN ONE handler (HandlerChain.cs, `if (grouping.Count() > 1)`). A lone
// [StickyHandler] handler produces no sticky chain at all, so every sticky permutation below needs a
// second handler for the same message type to be testing what it claims to test.

#region stores

public sealed class PermAModel
{
    public Guid Id { get; set; }
}

public sealed class PermBModel
{
    public Guid Id { get; set; }
}

public sealed class PermADbContext : DbContext
{
    public PermADbContext(DbContextOptions<PermADbContext> options) : base(options)
    {
    }

    public DbSet<PermAModel> Models => Set<PermAModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("perm_a");
        modelBuilder.Entity<PermAModel>().ToTable("models");
    }
}

public sealed class PermBDbContext : DbContext
{
    public PermBDbContext(DbContextOptions<PermBDbContext> options) : base(options)
    {
    }

    public DbSet<PermBModel> Models => Set<PermBModel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("perm_b");
        modelBuilder.Entity<PermBModel>().ToTable("models");
    }
}

#endregion

#region permutation: two sticky chains naming DIFFERENT stores (the GH-3886 shape)

public record PermSplitMessage(Guid Id);

[StickyHandler("perm-split-a")]
[Storage(typeof(PermADbContext))]
public sealed class PermSplitAHandler
{
    public void Handle(PermSplitMessage message, PermADbContext db) => db.Models.Add(new PermAModel { Id = message.Id });
}

[StickyHandler("perm-split-b")]
[Storage(typeof(PermBDbContext))]
public sealed class PermSplitBHandler
{
    public void Handle(PermSplitMessage message, PermBDbContext db) => db.Models.Add(new PermBModel { Id = message.Id });
}

#endregion

#region permutation: one sticky chain on an ancillary store, one on the MAIN store

public record PermMixedMessage(Guid Id);

[StickyHandler("perm-mixed-ancillary")]
[Storage(typeof(PermADbContext))]
public sealed class PermMixedAncillaryHandler
{
    public void Handle(PermMixedMessage message, PermADbContext db) => db.Models.Add(new PermAModel { Id = message.Id });
}

// Deliberately names no store and depends on no DbContext: this chain belongs to the MAIN store, and
// must not inherit its sibling's ancillary store through the message-type-wide fallback.
[StickyHandler("perm-mixed-main")]
public sealed class PermMixedMainHandler
{
    public static void Handle(PermMixedMessage message)
    {
    }
}

#endregion

#region permutation: sticky chains whose stores are INFERRED from enrolled DbContexts (GH-3870 x GH-3886)

public record PermInferredMessage(Guid Id);

// Neither handler carries a [Storage] attribute: taking the enrolled DbContext as a dependency is the
// only thing associating each chain with a store, and they must still be told apart per endpoint.
[StickyHandler("perm-inferred-b")]
public sealed class PermInferredHandler
{
    public void Handle(PermInferredMessage message, PermBDbContext db) => db.Models.Add(new PermBModel { Id = message.Id });
}

[StickyHandler("perm-inferred-a")]
public sealed class PermInferredOtherHandler
{
    public void Handle(PermInferredMessage message, PermADbContext db) => db.Models.Add(new PermAModel { Id = message.Id });
}

#endregion

#region permutation: a sticky chain alongside a non-sticky default chain

public record PermFallbackMessage(Guid Id);

[StickyHandler("perm-fallback-sticky")]
[Storage(typeof(PermADbContext))]
public sealed class PermFallbackStickyHandler
{
    public void Handle(PermFallbackMessage message, PermADbContext db) => db.Models.Add(new PermAModel { Id = message.Id });
}

public sealed class PermFallbackDefaultHandler
{
    public static void Handle(PermFallbackMessage message)
    {
    }
}

#endregion

#region permutation: two sticky chains AGREEING on one store (the GH-2576 shape)

public record PermAgreeMessage(Guid Id);

[StickyHandler("perm-agree-1")]
[Storage(typeof(PermADbContext))]
public sealed class PermAgreeOneHandler
{
    public void Handle(PermAgreeMessage message, PermADbContext db) => db.Models.Add(new PermAModel { Id = message.Id });
}

[StickyHandler("perm-agree-2")]
[Storage(typeof(PermADbContext))]
public sealed class PermAgreeTwoHandler
{
    public void Handle(PermAgreeMessage message, PermADbContext db) => db.Models.Add(new PermAModel { Id = message.Id });
}

#endregion

#region permutation: a plain non-sticky chain on an ancillary store

public record PermPlainMessage(Guid Id);

[Storage(typeof(PermBDbContext))]
public sealed class PermPlainHandler
{
    public void Handle(PermPlainMessage message, PermBDbContext db) => db.Models.Add(new PermBModel { Id = message.Id });
}

#endregion

public class sticky_handler_ancillary_store_routing : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<PermSplitAHandler>()
                    .IncludeType<PermSplitBHandler>()
                    .IncludeType<PermMixedAncillaryHandler>()
                    .IncludeType<PermMixedMainHandler>()
                    .IncludeType<PermInferredHandler>()
                    .IncludeType<PermInferredOtherHandler>()
                    .IncludeType<PermFallbackStickyHandler>()
                    .IncludeType<PermFallbackDefaultHandler>()
                    .IncludeType<PermAgreeOneHandler>()
                    .IncludeType<PermAgreeTwoHandler>()
                    .IncludeType<PermPlainHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();
                opts.UseEntityFrameworkCoreTransactions();

                opts.Services.AddDbContextWithWolverineIntegration<PermADbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "perm_a_wolverine");
                opts.Services.AddDbContextWithWolverineIntegration<PermBDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString), "perm_b_wolverine");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "perm_main");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "perm_a_wolverine", MessageStoreRole.Ancillary).Enroll<PermADbContext>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString,
                    "perm_b_wolverine", MessageStoreRole.Ancillary).Enroll<PermBDbContext>();

                opts.Services.AddResourceSetupOnStartup();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            }).StartAsync();

        _runtime = _host.GetRuntime();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private IMessageStore StoreA => _runtime.Stores!.FindAncillaryStore(typeof(PermADbContext));
    private IMessageStore StoreB => _runtime.Stores!.FindAncillaryStore(typeof(PermBDbContext));

    /// <summary>
    /// The endpoint a sticky handler was pinned to. Read off the chain rather than reconstructed from
    /// the endpoint name so the assertions use exactly the Uri the runtime registered.
    /// </summary>
    private Uri endpointFor<THandler>(Type messageType)
    {
        var chain = _runtime.Handlers.AllChains()
            .Single(c => c.MessageType == messageType &&
                         c.Handlers.Any(h => h.HandlerType == typeof(THandler)));

        chain.Endpoints.ShouldNotBeEmpty($"{typeof(THandler).Name} was expected to be a sticky handler");

        return chain.Endpoints.Single().Uri;
    }

    private IMessageStore? routedStore(Uri endpoint, Type messageType)
    {
        return _runtime.Stores!.TryFindAncillaryStoreForMessageType(endpoint, messageType.ToMessageTypeName());
    }

    [Fact]
    public void sticky_chains_naming_different_stores_each_route_to_their_own()
    {
        routedStore(endpointFor<PermSplitAHandler>(typeof(PermSplitMessage)), typeof(PermSplitMessage))
            .ShouldBeSameAs(StoreA);

        routedStore(endpointFor<PermSplitBHandler>(typeof(PermSplitMessage)), typeof(PermSplitMessage))
            .ShouldBeSameAs(StoreB);
    }

    [Fact]
    public void a_sticky_chain_on_the_main_store_does_not_inherit_its_siblings_ancillary_store()
    {
        routedStore(endpointFor<PermMixedAncillaryHandler>(typeof(PermMixedMessage)), typeof(PermMixedMessage))
            .ShouldBeSameAs(StoreA);

        // The whole point: this endpoint's handler names no store, so its inbox row belongs in the main
        // store. Falling through to the message-type-wide entry would hand it PermA's store instead.
        routedStore(endpointFor<PermMixedMainHandler>(typeof(PermMixedMessage)), typeof(PermMixedMessage))
            .ShouldBeNull();
    }

    [Fact]
    public void sticky_chains_infer_their_stores_from_enrolled_dbcontext_dependencies()
    {
        routedStore(endpointFor<PermInferredHandler>(typeof(PermInferredMessage)), typeof(PermInferredMessage))
            .ShouldBeSameAs(StoreB);

        routedStore(endpointFor<PermInferredOtherHandler>(typeof(PermInferredMessage)), typeof(PermInferredMessage))
            .ShouldBeSameAs(StoreA);
    }

    [Fact]
    public void an_endpoint_with_no_sticky_chain_falls_back_to_the_message_type_answer()
    {
        var sticky = endpointFor<PermFallbackStickyHandler>(typeof(PermFallbackMessage));
        routedStore(sticky, typeof(PermFallbackMessage)).ShouldBeSameAs(StoreA);

        // Any other endpoint -- here one that has no chain of its own for this message type -- keeps
        // resolving through the message-type keyed map exactly as it did before GH-3886
        routedStore(new Uri("rabbitmq://queue/some-unrelated-queue"), typeof(PermFallbackMessage))
            .ShouldBeSameAs(StoreA);
    }

    [Fact]
    public void sticky_chains_agreeing_on_one_store_both_route_to_it()
    {
        routedStore(endpointFor<PermAgreeOneHandler>(typeof(PermAgreeMessage)), typeof(PermAgreeMessage))
            .ShouldBeSameAs(StoreA);

        routedStore(endpointFor<PermAgreeTwoHandler>(typeof(PermAgreeMessage)), typeof(PermAgreeMessage))
            .ShouldBeSameAs(StoreA);
    }

    [Fact]
    public void a_non_sticky_chain_still_routes_by_message_type_from_any_endpoint()
    {
        routedStore(new Uri("rabbitmq://queue/anything"), typeof(PermPlainMessage)).ShouldBeSameAs(StoreB);
        routedStore(new Uri("local://whatever"), typeof(PermPlainMessage)).ShouldBeSameAs(StoreB);
    }

    [Fact]
    public void a_message_type_with_no_ancillary_association_stays_on_the_main_store()
    {
        _runtime.Stores!.TryFindAncillaryStoreForMessageType(new Uri("rabbitmq://queue/anything"),
            typeof(PermUnmappedMessage).ToMessageTypeName()).ShouldBeNull();
    }
}

public record PermUnmappedMessage(Guid Id);
