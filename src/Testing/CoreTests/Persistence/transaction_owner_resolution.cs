using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
///     GH-4631. The store-agnostic half of the two-provider rule, with no database of any kind: given two
///     providers that both claim a chain, a designation picks the owner and its absence is an error.
/// </summary>
/// <remarks>
///     The end-to-end proof lives in <c>PersistenceTests.two_provider_transaction_designation</c>, which
///     needs Postgres and SQL Server. This suite exists because the rule is not EF-Core-and-Marten
///     specific — RavenDb and Cosmos DB also claim ordinary chains and also implement
///     <see cref="IPersistenceFrameProvider.OwnsStorageType" /> — and because a provider pair that cannot
///     resolve a designation would hand the user a hard build failure whose stated remedy does nothing.
/// </remarks>
public class transaction_owner_resolution
{
    private static readonly HandlerChain theChain = HandlerChain.For<FakeHandler>(x => x.Handle(null!), null!);

    private static IPersistenceFrameProvider resolve(params IPersistenceFrameProvider[] potentials)
        => TransactionOwnerResolution.SelectDesignatedOwner(theChain, potentials, null!);

    [Fact]
    public void a_transactional_designation_picks_the_provider_that_owns_the_named_type()
    {
        var owner = new OwningProvider(typeof(TheStore));
        var other = new OwningProvider(typeof(SomeOtherStore));

        theChain.Tags[TransactionalAttribute.TransactionalDbContextTypeKey] = typeof(TheStore);

        try
        {
            resolve(other, owner).ShouldBeSameAs(owner);
        }
        finally
        {
            theChain.Tags.Remove(TransactionalAttribute.TransactionalDbContextTypeKey);
        }
    }

    [Fact]
    public void no_designation_at_all_throws_naming_every_candidate()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            resolve(new OwningProvider(typeof(TheStore)), new OwningProvider(typeof(SomeOtherStore))));

        // Naming the candidates is the whole point: "Wolverine could not decide" is unactionable on its own
        ex.Message.ShouldContain("OwningProvider");
        ex.Message.ShouldContain("[Transactional(typeof(YourStorageType))]");
        ex.Message.ShouldContain("[NonTransactional]");
    }

    [Fact]
    public void a_designation_no_candidate_owns_says_so_rather_than_just_repeating_itself()
    {
        theChain.Tags[TransactionalAttribute.TransactionalDbContextTypeKey] = typeof(UnownedStore);

        try
        {
            var ex = Should.Throw<InvalidOperationException>(() =>
                resolve(new OwningProvider(typeof(TheStore)), new OwningProvider(typeof(SomeOtherStore))));

            ex.Message.ShouldContain(nameof(UnownedStore));
            ex.Message.ShouldContain("is not owned by any of them");
        }
        finally
        {
            theChain.Tags.Remove(TransactionalAttribute.TransactionalDbContextTypeKey);
        }
    }

    public class TheStore;

    public class SomeOtherStore;

    public class UnownedStore;

    public record TheMessage;

    public class FakeHandler
    {
        public void Handle(TheMessage message)
        {
        }
    }

    // Stands in for any integration that claims ordinary chains -- EF Core, Marten, RavenDb, Cosmos DB.
    // Each one answers OwnsStorageType for the shape of type it is designated by.
    private class OwningProvider : IPersistenceFrameProvider
    {
        private readonly Type _storageType;

        public OwningProvider(Type storageType)
        {
            _storageType = storageType;
        }

        public bool OwnsStorageType(Type storageType, IServiceContainer container) => storageType == _storageType;

        public bool CanApply(IChain chain, IServiceContainer container) => true;

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container) =>
            throw new NotSupportedException();

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType) =>
            throw new NotSupportedException();

        public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService) =>
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
