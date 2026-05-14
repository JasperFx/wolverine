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

            var enrollFrame =
                typeof(EnrollAndFetchSagaStorageFrame<,>).CloseAndBuildAs<Frame>(idType!, sagaChain.SagaType);

            sagaChain.Middleware.Add(enrollFrame);
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

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