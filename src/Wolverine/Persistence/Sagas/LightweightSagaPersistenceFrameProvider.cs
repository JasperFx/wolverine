using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public class LightweightSagaPersistenceFrameProvider : IPersistenceFrameProvider
{
    // GH-3443: this provider claims EVERY saga (CanApply is true for any SagaChain, CanPersist for any
    // Saga type), so it is a catch-all exactly like the Marten / RavenDb / Polecat / CosmosDb / in-memory
    // providers - all of which set this. It was the one that missed the override, which left it sorting
    // AHEAD of Marten in OrderedPersistenceProviders and silently stealing sagas Marten should own.
    public bool IsCatchAll => true;

    // ApplyTransactionSupport closes EnrollAndFetchSagaStorageFrame<,> over
    // (idType, sagaType) at codegen time; CanPersist closes ISagaStorage<,>
    // over the same. AOT-clean apps in TypeLoadMode.Static run pre-generated
    // frames where these closures are baked in by source-generated registration;
    // the IPersistenceFrameProvider surface only fires under Dynamic codegen,
    // which is intentionally not AOT-clean (see AOT publishing guide). Same
    // chunk M / chunk P pattern.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "EnrollAndFetchSagaStorageFrame<,> closed over runtime saga types during Dynamic codegen; AOT consumers run pre-generated frames. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "EnrollAndFetchSagaStorageFrame<,> closed over runtime saga types during Dynamic codegen; AOT consumers run pre-generated frames. See AOT guide.")]
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        // Idempotent here just in case
        if (chain.Middleware.OfType<ISagaStorageFrame>().Any()) return;

        if (chain is SagaChain sagaChain)
        {
            var member = SagaChain.DetermineSagaIdMember(sagaChain.SagaType, sagaChain.SagaType);
            if (member == null)
            {
                throw new InvalidOperationException(
                    $"Wolverine is unable to determine a public identity member for the Saga type {sagaChain.SagaType}");
            }

            var idType = member.GetRawMemberType();

            // GH-4805. Ask the store whether it wants generated code to build its saga schema directly.
            // A store that says yes gets a frame that renders the construction as source text, which is
            // the only shape NativeAOT can execute -- the runtime path goes through an abstract generic
            // method whose generic-virtual dispatch ILC cannot resolve. A store with no opinion, or no
            // store resolvable here at all, keeps the original path unchanged.
            var codegen = tryFindSagaStorageCodeSource(container)
                ?.SagaSchemaCodegenFor(sagaChain.SagaType, idType!);

            // GH-4805. Constructed directly, not closed with CloseAndBuildAs: the frame is no longer
            // generic, which is what stops ILC needing an instantiation it will not generate code for.
            sagaChain.Middleware.Add(
                new EnrollAndFetchSagaStorageFrame(idType!, sagaChain.SagaType, codegen));
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    /// <summary>
    ///     GH-4805. The live message store, if it has an opinion about how its saga schema should be built
    ///     in generated code.
    /// </summary>
    /// <remarks>
    ///     Swallows a resolution failure on purpose. This runs during policy application, and a host whose
    ///     runtime cannot hand over a store yet -- or at all -- must still get a working chain on the
    ///     original path rather than a startup exception from an optimisation.
    /// </remarks>
    private static ISagaStorageCodeSource? tryFindSagaStorageCodeSource(IServiceContainer container)
    {
        try
        {
            return (container.Services.GetService(typeof(IWolverineRuntime)) as IWolverineRuntime)?.Storage
                as ISagaStorageCodeSource;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// GH-4631. Unlike the other saga-oriented providers, <see cref="CanApply"/> here is not saga-chains-
    /// only: an ORDINARY handler that injects <c>ISagaStorage&lt;TSaga,TId&gt;</c> directly also claims a
    /// chain, so this genuinely co-applies with (say) EF Core on a handler that takes both. There is no
    /// store marker type to name, so the designation is the closed saga-storage interface itself:
    /// <c>[Transactional(typeof(ISagaStorage&lt;MySaga, Guid&gt;))]</c>.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Same interface-closure scan as CanApply below, over a type the developer named in a [Transactional]/[Storage] designation; AOT consumers register saga storage types explicitly via the AOT publishing guide.")]
    public bool OwnsStorageType(Type storageType, IServiceContainer container)
    {
        return storageType.Closes(typeof(ISagaStorage<,>));
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Service-dependency types flow from registered persistence-frame providers; AOT consumers register saga storage types explicitly via the AOT publishing guide so the interface-closure scan resolves against statically-rooted types.")]
    public bool CanApply(IChain chain, IServiceContainer container)
    {
        return chain is SagaChain || chain.ServiceDependencies(container, []).Any(x => x.Closes(typeof(ISagaStorage<,>)));
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "ISagaStorage<,> closed over runtime saga types during Dynamic codegen; AOT consumers register saga types explicitly. See AOT guide.")]
    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        if (entityType.CanBeCastTo<Saga>())
        {
            var idType = SagaChain.DetermineSagaIdMember(entityType, entityType)?.GetRawMemberType();
            if (idType == null)
            {
                persistenceService = default!;
                return false;
            }

            persistenceService = typeof(ISagaStorage<,>).MakeGenericType(idType, entityType);
            return true;
        }

        persistenceService = default!;
        return false;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType() ?? throw new ArgumentException(nameof(sagaType), $"Unable to determine the identity member for {sagaType.FullNameInCode()}");
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        return new LoadSagaOperation(sagaType, sagaId);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperation(saga, SagaOperationType.InsertAsync);
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("No additional Unit of Work necessary");
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return new SagaOperation(saga, SagaOperationType.UpdateAsync);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        return new SagaOperation(saga, SagaOperationType.DeleteAsync);
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return new SagaOperation(variable, SagaOperationType.DeleteAsync);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        throw new NotSupportedException();
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];
}