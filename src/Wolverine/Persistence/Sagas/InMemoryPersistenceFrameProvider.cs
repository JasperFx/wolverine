using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public class InMemoryPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        // Nothing
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        // Nothing
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        return false;
    }

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = GetType();
        return true;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        return SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetMemberType() ?? typeof(object);
    }

    // The DetermineXxxFrame methods are codegen-time helpers that close
    // InMemorySagaPersistor.{Load,Store,Delete}<T> over the runtime-resolved
    // saga type to emit MethodCall frames. AOT-clean apps run pre-generated
    // handler code in TypeLoadMode.Static where the closed instantiations are
    // baked into the source-generated registration; the IPersistenceFrameProvider
    // surface only fires under Dynamic codegen, which is intentionally not
    // AOT-clean (see AOT publishing guide). Leaf suppression matches the
    // chunk M (LoggerVariableSource) precedent for Dynamic-mode codegen
    // helpers.
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "InMemorySagaPersistor.Load<T> closed over runtime sagaType during Dynamic codegen; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        var method = typeof(InMemorySagaPersistor).GetMethod(nameof(InMemorySagaPersistor.Load))!
            .MakeGenericMethod(sagaType);

        var call = new MethodCall(typeof(InMemorySagaPersistor), method)
        {
            Arguments =
            {
                [0] = sagaId
            }
        };

        return call;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "InMemorySagaPersistor.Store<T> closed over runtime saga type during Dynamic codegen; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2076",
        Justification = "saga.VariableType is the user's saga type, statically rooted by handler discovery; PublicProperties requirement is satisfied via the generated frame's static type knowledge.")]
    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        var method = typeof(InMemorySagaPersistor).GetMethod(nameof(InMemorySagaPersistor.Store))!
            .MakeGenericMethod(saga.VariableType);
        var call = new MethodCall(typeof(InMemorySagaPersistor), method)
        {
            Arguments =
            {
                [0] = saga
            }
        };

        return call;
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        return new CommentFrame("No unit of work");
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        return DetermineInsertFrame(saga, container);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "InMemorySagaPersistor.Delete<T> closed over runtime saga type during Dynamic codegen; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        var method = typeof(InMemorySagaPersistor).GetMethod(nameof(InMemorySagaPersistor.Delete))!
            .MakeGenericMethod(saga.VariableType);

        // This guy is pretty limited
        sagaId ??= new Variable(typeof(object), $"{saga.Usage}.Id");
        
        var call = new MethodCall(typeof(InMemorySagaPersistor), method)
        {
            Arguments =
            {
                [0] = sagaId
            }
        };

        return call;
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        return DetermineInsertFrame(saga, container);
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        return DetermineDeleteFrame(null!, variable, container);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "InMemorySagaPersistorStore<> closed over runtime entityType during Dynamic codegen; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "InMemorySagaPersistorStore<> closed over runtime entityType during Dynamic codegen; AOT consumers run pre-generated frames in TypeLoadMode.Static. See AOT guide.")]
    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var call = typeof(InMemorySagaPersistorStore<>).CloseAndBuildAs<MethodCall>(entityType);
        call.Arguments[0] = action;
        return call;
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];
}

// T forwards the [DAM(PublicProperties)] requirement from the underlying
// InMemorySagaPersistor.StoreAction<T> call so callers don't need to suppress
// IL2091 at every InMemorySagaPersistorStore<entityType> closure site.
internal class InMemorySagaPersistorStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T> : MethodCall
{
    public InMemorySagaPersistorStore() : base(typeof(InMemorySagaPersistor), ReflectionHelper.GetMethod<InMemorySagaPersistor>(x => x.StoreAction(Storage.Nothing<T>()))!)
    {
    }
}