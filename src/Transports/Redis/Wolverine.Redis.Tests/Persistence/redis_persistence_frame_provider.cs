using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Redis.Internal;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// One application using <c>[Entity]</c> against both a document store and Redis. The Redis provider is
/// selective, so it is consulted ahead of a catch-all store and claims only what was registered;
/// everything else falls through. Whichever order the two integrations were registered in.
/// </summary>
/// <remarks>
/// The catch-all here is a stub standing in for Marten rather than Marten itself: the question is
/// purely about consultation order, and registering the real thing would put a Postgres dependency on
/// tests that need no server at all. Real-store permutations of the same rule live in
/// PersistenceTests/persistence_provider_precedence_permutations.cs, and the rule itself in
/// CoreTests/Persistence/persistence_provider_precedence.cs.
/// </remarks>
public class redis_persistence_frame_provider
{
    private record InRedis(string Id);

    private record InTheOtherStore(string Id);

    public class OwnedSaga : Saga
    {
        public string Id { get; set; } = null!;

        public void Handle(TouchOwnedSaga message)
        {
        }
    }

    public class SomebodyElsesSaga : Saga
    {
        public string Id { get; set; } = null!;

        public void Handle(TouchSomebodyElsesSaga message)
        {
        }
    }

    private static IServiceContainer containerWith(Action<RedisPersistenceConfiguration>? configure = null)
    {
        var configuration = new RedisPersistenceConfiguration();
        configuration.Store<InRedis>(x => x.KeyFor = ctx => $"doc:{ctx.Id}");
        configuration.Saga<OwnedSaga>(x => x.KeyFor = ctx => $"saga:{ctx.Id}");
        configure?.Invoke(configuration);

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IServiceContainer, ServiceContainer>();

        return services.BuildServiceProvider().GetRequiredService<IServiceContainer>();
    }

    private static GenerationRules rulesWith(params IPersistenceFrameProvider[] providers)
    {
        var rules = new GenerationRules();
        rules.Properties[GenerationRulesExtensions.PersistenceKey] = providers.ToList();
        return rules;
    }

    [Fact]
    public void redis_wins_for_a_registered_document_whichever_order_the_stores_were_registered_in()
    {
        var container = containerWith();

        foreach (var rules in new[]
                 {
                     rulesWith(new CatchAllStore(), new RedisPersistenceFrameProvider()),
                     rulesWith(new RedisPersistenceFrameProvider(), new CatchAllStore())
                 })
        {
            rules.TryFindPersistenceFrameProvider(container, typeof(InRedis), out var provider).ShouldBeTrue();

            provider.ShouldBeOfType<RedisPersistenceFrameProvider>();
        }
    }

    [Fact]
    public void everything_redis_did_not_claim_falls_through_to_the_document_store()
    {
        var other = new CatchAllStore();
        var rules = rulesWith(new RedisPersistenceFrameProvider(), other);

        rules.TryFindPersistenceFrameProvider(containerWith(), typeof(InTheOtherStore), out var provider)
            .ShouldBeTrue();

        provider.ShouldBeSameAs(other);
    }

    /// <summary>
    /// Registering Redis persistence without registering any type leaves the rest of the application
    /// exactly as it was.
    /// </summary>
    [Fact]
    public void an_empty_registration_claims_nothing_at_all()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RedisPersistenceConfiguration());
        services.AddSingleton<IServiceCollection>(services);
        services.AddSingleton<IServiceContainer, ServiceContainer>();
        var container = services.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        var other = new CatchAllStore();
        var rules = rulesWith(new RedisPersistenceFrameProvider(), other);

        rules.TryFindPersistenceFrameProvider(container, typeof(InRedis), out var provider).ShouldBeTrue();

        provider.ShouldBeSameAs(other);
    }

    /// <summary>
    /// The load-bearing half of the Store/Saga split. <c>CanApply</c> is the same question
    /// <c>[Transactional]</c> and <c>AutoApplyTransactions</c> ask, and Redis has no transaction to own,
    /// so an ordinary chain must never resolve here — it would take the transaction away from the store
    /// that actually has one.
    /// </summary>
    [Fact]
    public void claims_a_registered_saga_chain_and_nothing_else()
    {
        var container = containerWith();
        var provider = new RedisPersistenceFrameProvider();

        provider.CanApply(sagaChainFor<OwnedSaga>(), container).ShouldBeTrue();

        // A saga somebody else's store owns
        provider.CanApply(sagaChainFor<SomebodyElsesSaga>(), container).ShouldBeFalse();

        // And an ordinary handler chain, even one whose entity IS registered in Redis
        provider.CanApply(anOrdinaryChain(), container).ShouldBeFalse();
    }

    [Fact]
    public void a_registered_saga_chain_beats_a_catch_all_store()
    {
        var rules = rulesWith(new RedisPersistenceFrameProvider(), new CatchAllStore { AppliesToChains = true });

        rules.GetPersistenceProviders(sagaChainFor<OwnedSaga>(), containerWith())
            .ShouldBeOfType<RedisPersistenceFrameProvider>();
    }

    /// <summary>
    /// A saga read has to bring its revision back with it, or the write that follows has nothing to
    /// compare against — while a plain document read must not declare a revision local nobody uses.
    /// </summary>
    [Fact]
    public void a_saga_loads_with_its_revision_and_a_document_does_not()
    {
        var container = containerWith();
        var provider = new RedisPersistenceFrameProvider();

        provider.DetermineLoadFrame(container, typeof(OwnedSaga), new Variable(typeof(string), "id"))
            .ShouldBeOfType<LoadRedisSagaFrame>();

        provider.DetermineLoadFrame(container, typeof(InRedis), new Variable(typeof(string), "id"))
            .ShouldBeOfType<LoadRedisDocumentFrame>();
    }

    /// <summary>
    /// Compare-and-swap belongs to the saga chain, which read the revision it is writing against. A
    /// storage action hands the provider a synthetic member access with no preceding read, so it stays
    /// last-write-wins rather than comparing against a revision that does not exist.
    /// </summary>
    [Fact]
    public void only_a_saga_wolverine_itself_read_gets_compare_and_swap()
    {
        var container = containerWith();
        var provider = new RedisPersistenceFrameProvider();

        var saga = new Variable(typeof(OwnedSaga), "sagaState");
        provider.DetermineInsertFrame(saga, container).ShouldBeOfType<RedisSagaInsertFrame>();
        provider.DetermineUpdateFrame(saga, container).ShouldBeOfType<RedisSagaUpdateFrame>();
        provider.DetermineDeleteFrame(new Variable(typeof(string), "sagaId"), saga, container)
            .ShouldBeOfType<RedisSagaDeleteFrame>();

        var fromAnAction = new Variable(typeof(OwnedSaga), "update1.Entity");
        provider.DetermineUpdateFrame(fromAnAction, container).ShouldBeOfType<RedisWriteFrame>();

        // Storage.Store() is an explicit "just write it" side effect, not the saga update path
        provider.DetermineStoreFrame(saga, container).ShouldBeOfType<RedisWriteFrame>();
    }

    [Fact]
    public void the_identity_type_comes_from_the_registration_rather_than_being_hardcoded()
    {
        var container = containerWith(configuration =>
            configuration.Store<GuidIdentified>(x => x.KeyFor = ctx => $"g:{ctx.Id}"));

        new RedisPersistenceFrameProvider().DetermineSagaIdType(typeof(GuidIdentified), container)
            .ShouldBe(typeof(Guid));
    }

    public record GuidIdentified(Guid Id);

    private static SagaChain sagaChainFor<T>() where T : Saga
    {
        var options = new WolverineOptions();

        return new SagaChain(new HandlerCall(typeof(T), typeof(T).GetMethod("Handle")!),
            options.HandlerGraph, []);
    }

    private static HandlerChain anOrdinaryChain()
    {
        var options = new WolverineOptions();

        return new HandlerChain(typeof(TouchInRedis), options.HandlerGraph);
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

public record TouchInRedis(string Id);

public record TouchOwnedSaga(string Id);

public record TouchSomebodyElsesSaga(string Id);
