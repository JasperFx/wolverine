using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

/// <summary>
/// Marks a return value as potentially modifying the wrapped entity in the underlying persistence
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IStorageAction<T> : ISideEffectAware
{
    StorageAction Action { get; }
    T Entity { get; }

    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules, IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            provider.ApplyTransactionSupport(chain, container, typeof(T));
            var value = new EntityVariable(variable);
            return provider.DetermineStorageActionFrame(typeof(T), value, container).WrapIfNotNull(variable);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }
}

/// <summary>
/// Special side effect return type for Wolverine when you want to carry out zero
/// to many storage actions of the same entity type
/// </summary>
/// <typeparam name="T"></typeparam>
public class UnitOfWork<T> : List<IStorageAction<T>>, ISideEffectAware
{
    /// <summary>
    /// "Upsert" an entity. Note that not every persistence tool natively supports
    /// upsert operations
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public UnitOfWork<T> Store(T entity)
    {
        Add(Storage.Store(entity));
        return this;
    }

    /// <summary>
    /// "Insert" a new entity to the underlying persistence mechanism
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public UnitOfWork<T> Insert(T entity)
    {
        Add(Storage.Insert(entity));
        return this;
    }

    /// <summary>
    /// "Update" the entity into the underlying persistence mechanism
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public UnitOfWork<T> Update(T entity)
    {
        Add(Storage.Update(entity));
        return this;
    }

    /// <summary>
    /// "Delete" the entity in the underlying persistence mechanism
    /// </summary>
    /// <param name="entity"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public UnitOfWork<T> Delete(T entity)
    {
        Add(Storage.Delete(entity));
        return this;
    }
    
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules, IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            provider.ApplyTransactionSupport(chain, container, typeof(T));
            var element = new Variable(typeof(T), "item_of_" + variable.Usage);
            var inner = provider.DetermineStorageActionFrame(typeof(T), element, container);

            return new ForEachStorageActionFrame(variable, element, inner).WrapIfNotNull(variable);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }
}

internal class ForEachStorageActionFrame : Frame
{
    private readonly Variable _collection;
    private readonly Variable _element;
    private readonly Frame _inner;
    
    public ForEachStorageActionFrame(Variable collection, Variable element, Frame inner) : base(inner.IsAsync)
    {
        _collection = collection;
        _element = element;
        _inner = inner;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _collection;
        foreach (var inner in _inner.FindVariables(chain))
        {
            yield return inner;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:foreach (var {_element.Usage} in {_collection.Usage})");
        _inner.GenerateCode(method, writer);
        writer.FinishBlock();
        
        Next?.GenerateCode(method, writer);
    }
}