using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Codegen;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence;

public static class Storage
{
    public static Store<T> Store<T>(T entity) => new Store<T>(entity);
    public static Insert<T> Insert<T>(T entity) => new Insert<T>(entity);
    public static Update<T> Update<T>(T entity) => new Update<T>(entity);
    public static Delete<T> Delete<T>(T entity) => new Delete<T>(entity);
    public static Nothing<T> Nothing<T>(T entity) => new Nothing<T>(entity);
}

public class NoMatchingPersistenceProviderException : Exception
{
    public NoMatchingPersistenceProviderException(Type entityType) : base($"Wolverine is unable to determine a persistence provider for entity type {entityType.FullNameInCode()}")
    {
    }
}

public interface IStorageAction<T> : ISideEffectAware
{
    StorageAction Action { get; }
    T Entity { get; }
}

public record Nothing<T>(T Entity) : IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules, IServiceContainer container)
    {
        return new CommentFrame("Do nothing with the entity");
    }

    public StorageAction Action => StorageAction.Nothing;
}

public record Store<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            var value = new MemberAccessVariable(variable, ReflectionHelper.GetProperty<Store<T>>(x => x.Entity));
            return provider.DetermineStoreFrame(value, container);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }

    public StorageAction Action => StorageAction.Store;
}

public record Delete<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            var value = new MemberAccessVariable(variable, ReflectionHelper.GetProperty<Delete<T>>(x => x.Entity));
            return provider.DetermineDeleteFrame(value, container);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));

    }
    
    public StorageAction Action => StorageAction.Delete;
}

public record Insert<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            var value = new MemberAccessVariable(variable, ReflectionHelper.GetProperty<Insert<T>>(x => x.Entity));
            return provider.DetermineInsertFrame(value, container);
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }
    
    public StorageAction Action => StorageAction.Insert;
}

public record Update<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            var value = new MemberAccessVariable(variable, ReflectionHelper.GetProperty<Update<T>>(x => x.Entity));
            var frame = provider.DetermineUpdateFrame(value, container);
            return frame;
        }

        throw new NoMatchingPersistenceProviderException(typeof(T));
    }
    
    public StorageAction Action => StorageAction.Update;
}

public enum StorageAction
{
    Store,
    Delete,
    Nothing,
    Update,
    Insert
}