using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Xunit;

namespace CoreTests.Persistence;

// Unit tests for the GH-3359 consultation-order rule: selective persistence providers
// (whose CanPersist checks the entity type against their own model, like EF Core) are
// consulted ahead of catch-all document stores (whose CanPersist claims any type, like
// Marten) regardless of the order the integrations were registered in. Within each
// group, registration order is preserved.
public class persistence_provider_precedence
{
    private static GenerationRules rulesWith(params IPersistenceFrameProvider[] providers)
    {
        var rules = new GenerationRules();
        rules.Properties[GenerationRulesExtensions.PersistenceKey] = providers.ToList();
        return rules;
    }

    [Fact]
    public void selective_provider_wins_for_its_entity_even_when_the_catch_all_registered_after_it()
    {
        var selective = new SelectiveProvider(typeof(MappedEntity));
        var catchAll = new CatchAllProvider();

        // The catch-all integration registered last, so InsertFirstPersistenceStrategy put it
        // at index 0 - the registration order that used to steal the entity from the selective
        // provider
        var rules = rulesWith(catchAll, selective);

        rules.TryFindPersistenceFrameProvider(null!, typeof(MappedEntity), out var provider)
            .ShouldBeTrue();

        provider.ShouldBeSameAs(selective);
    }

    [Fact]
    public void selective_provider_wins_for_its_entity_when_it_registered_last_too()
    {
        var selective = new SelectiveProvider(typeof(MappedEntity));
        var catchAll = new CatchAllProvider();

        var rules = rulesWith(selective, catchAll);

        rules.TryFindPersistenceFrameProvider(null!, typeof(MappedEntity), out var provider)
            .ShouldBeTrue();

        provider.ShouldBeSameAs(selective);
    }

    [Fact]
    public void unmapped_entity_still_falls_through_to_the_catch_all()
    {
        var selective = new SelectiveProvider(typeof(MappedEntity));
        var catchAll = new CatchAllProvider();

        var rules = rulesWith(catchAll, selective);

        // Nothing the selective provider maps, so the catch-all keeps everything else
        rules.TryFindPersistenceFrameProvider(null!, typeof(UnmappedDocument), out var provider)
            .ShouldBeTrue();

        provider.ShouldBeSameAs(catchAll);
    }

    [Fact]
    public void registration_order_is_preserved_within_each_group()
    {
        var selective1 = new SelectiveProvider(typeof(MappedEntity));
        var selective2 = new SelectiveProvider(typeof(MappedEntity));
        var catchAll1 = new CatchAllProvider();
        var catchAll2 = new CatchAllProvider();

        var rules = rulesWith(catchAll1, selective1, catchAll2, selective2);

        var ordered = rules.OrderedPersistenceProviders();

        // OrderBy is a stable sort: selective providers first in their original relative
        // order, catch-alls after in theirs
        ordered.ShouldBe([selective1, selective2, catchAll1, catchAll2]);
    }

    [Fact]
    public void a_lone_catch_all_is_still_found()
    {
        var catchAll = new CatchAllProvider();
        var rules = rulesWith(catchAll);

        rules.TryFindPersistenceFrameProvider(null!, typeof(MappedEntity), out var provider)
            .ShouldBeTrue();

        provider.ShouldBeSameAs(catchAll);
    }

    // GH-3443: the real lightweight saga provider claims every saga, so it must be treated as a
    // catch-all like Marten - otherwise it sorts ahead of Marten and steals sagas Marten should own.
    [Fact]
    public void the_lightweight_saga_provider_is_a_catch_all()
    {
        // Cast to the interface deliberately: IsCatchAll is a default interface member, so reading it off
        // the concrete type would only compile when the override is present. Through the interface it
        // reads the default (false) when the override is missing, which is the real regression.
        ((IPersistenceFrameProvider)new LightweightSagaPersistenceFrameProvider()).IsCatchAll.ShouldBeTrue();
    }

    [Fact]
    public void marten_sorts_ahead_of_the_lightweight_saga_provider()
    {
        // A relational message store registers the lightweight provider (appended); Marten registers
        // via InsertFirstPersistenceStrategy, landing at index 0. This is that raw order.
        var martenLike = new CatchAllProvider();
        var lightweight = new LightweightSagaPersistenceFrameProvider();

        var rules = rulesWith(martenLike, lightweight);

        var ordered = rules.OrderedPersistenceProviders();

        // Both are catch-alls, so the stable sort preserves the raw order: Marten first, lightweight
        // last. Before the fix, lightweight (mis-reported as non-catch-all) sorted FIRST and won every
        // saga, silently moving Marten-owned sagas onto the lightweight tables.
        ordered[0].ShouldBeSameAs(martenLike);
        ordered[^1].ShouldBeSameAs(lightweight);
    }

    [Fact]
    public void an_ef_core_like_selective_provider_still_outranks_marten_and_lightweight()
    {
        // The GH-3359 rule must survive the fix: a selective provider (EF Core, keyed on a DbContext
        // mapping) is consulted before either catch-all.
        var efCoreLike = new SelectiveProvider(typeof(MappedEntity));
        var martenLike = new CatchAllProvider();
        var lightweight = new LightweightSagaPersistenceFrameProvider();

        var rules = rulesWith(martenLike, lightweight, efCoreLike);

        rules.OrderedPersistenceProviders()[0].ShouldBeSameAs(efCoreLike);
    }

    public class MappedEntity;

    public class UnmappedDocument;

    // Claims only the single entity type it was constructed with, like EF Core claiming
    // only the types mapped in a registered DbContext model
    private class SelectiveProvider : StubProviderBase
    {
        private readonly Type _entityType;

        public SelectiveProvider(Type entityType)
        {
            _entityType = entityType;
        }

        public override bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
        {
            persistenceService = GetType();
            return entityType == _entityType;
        }
    }

    // Claims every type it is asked about, like Marten
    private class CatchAllProvider : StubProviderBase
    {
        public override bool IsCatchAll => true;

        public override bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
        {
            persistenceService = GetType();
            return true;
        }
    }

    private abstract class StubProviderBase : IPersistenceFrameProvider
    {
        public virtual bool IsCatchAll => false;

        public abstract bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService);

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container) =>
            throw new NotSupportedException();

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType) =>
            throw new NotSupportedException();

        public bool CanApply(IChain chain, IServiceContainer container) => false;

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
