using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.AzureBlobStorage.Internals;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// One application using [Entity] against both a document store and Azure Blob Storage. The blob
/// provider is selective, so it is consulted ahead of a catch-all store and claims only what was
/// registered; everything else falls through. Whichever order the two integrations were registered in.
/// </summary>
/// <remarks>
/// The catch-all here is a stub standing in for Marten rather than Marten itself: the question is
/// purely about consultation order, and registering the real thing would put a Postgres dependency on
/// a suite whose CI target only starts Azurite. Real-store permutations of the same rule live in
/// PersistenceTests/persistence_provider_precedence_permutations.cs, and the rule itself in
/// CoreTests/Persistence/persistence_provider_precedence.cs.
/// </remarks>
public class mixed_persistence_precedence
{
    private record InBlobStorage(string Id);

    private record InTheOtherStore(string Id);

    private static IServiceContainer theContainer
    {
        get
        {
            var configuration = new AzureBlobStorageConfiguration();
            configuration.Store<InBlobStorage>(x =>
            {
                x.ContainerName = "some-container";
                x.BlobNameFor = ctx => $"{ctx.Id}.json";
            });

            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddSingleton<IServiceCollection>(services);
            services.AddSingleton<IServiceContainer, ServiceContainer>();

            return services.BuildServiceProvider().GetRequiredService<IServiceContainer>();
        }
    }

    private static GenerationRules rulesWith(params IPersistenceFrameProvider[] providers)
    {
        var rules = new GenerationRules();
        rules.Properties[GenerationRulesExtensions.PersistenceKey] = providers.ToList();
        return rules;
    }

    [Fact]
    public void blob_storage_wins_for_a_registered_document_with_the_document_store_registered_first()
    {
        var blobs = new BlobPersistenceFrameProvider();
        var rules = rulesWith(new CatchAllStore(), blobs);

        rules.TryFindPersistenceFrameProvider(theContainer, typeof(InBlobStorage), out var provider).ShouldBeTrue();

        provider.ShouldBeSameAs(blobs);
    }

    [Fact]
    public void blob_storage_wins_for_a_registered_document_with_blob_storage_registered_first()
    {
        var blobs = new BlobPersistenceFrameProvider();
        var rules = rulesWith(blobs, new CatchAllStore());

        rules.TryFindPersistenceFrameProvider(theContainer, typeof(InBlobStorage), out var provider).ShouldBeTrue();

        provider.ShouldBeSameAs(blobs);
    }

    [Fact]
    public void everything_blob_storage_did_not_claim_falls_through_to_the_document_store()
    {
        var other = new CatchAllStore();
        var rules = rulesWith(new BlobPersistenceFrameProvider(), other);

        rules.TryFindPersistenceFrameProvider(theContainer, typeof(InTheOtherStore), out var provider)
            .ShouldBeTrue();

        provider.ShouldBeSameAs(other);
    }

    /// <summary>
    /// Registering blob storage without registering any document type leaves the rest of the
    /// application exactly as it was.
    /// </summary>
    [Fact]
    public void an_empty_blob_registration_claims_nothing_at_all()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AzureBlobStorageConfiguration());
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IServiceContainer, ServiceContainer>();
        var container = services.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        var other = new CatchAllStore();
        var rules = rulesWith(new BlobPersistenceFrameProvider(), other);

        rules.TryFindPersistenceFrameProvider(container, typeof(InBlobStorage), out var provider).ShouldBeTrue();

        provider.ShouldBeSameAs(other);
    }

    /// <summary>
    /// Sagas resolve on CanApply rather than CanPersist, and the blob provider claims a chain only for a
    /// type registered through Saga&lt;T&gt;() — so an UNREGISTERED saga in a mixed application still
    /// belongs to the transactional store. This is what keeps [Transactional] and AutoApplyTransactions
    /// out of a provider that has nothing to commit.
    /// </summary>
    [Fact]
    public void sagas_this_package_did_not_claim_still_belong_to_the_document_store()
    {
        var other = new CatchAllStore { AppliesToChains = true };
        var rules = rulesWith(new BlobPersistenceFrameProvider(), other);

        rules.GetPersistenceProviders(null!, theContainer).ShouldBeSameAs(other);
    }

    // Claims every type it is asked about, like Marten.
    private class CatchAllStore : IPersistenceFrameProvider
    {
        public bool AppliesToChains { get; init; }

        public bool IsCatchAll => true;

        public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
        {
            persistenceService = GetType();
            return true;
        }

        public bool CanApply(IChain chain, IServiceContainer container) => AppliesToChains;

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container) =>
            throw new NotSupportedException();

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType) =>
            throw new NotSupportedException();

        public Type DetermineSagaIdType(Type sagaType, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId) =>
            throw new NotSupportedException();

        public Frame DetermineInsertFrame(Variable saga, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame DetermineStoreFrame(Variable saga, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container) =>
            throw new NotSupportedException();

        public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) =>
            throw new NotSupportedException();
    }
}
