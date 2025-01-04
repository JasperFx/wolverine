using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using InvalidOperationException = System.InvalidOperationException;

namespace Wolverine.Persistence;

public class InvalidEntityLoadUsageException : Exception
{
    public InvalidEntityLoadUsageException(EntityAttribute att, ParameterInfo parameter) : base($"Unable to determine a value variable named '{att.ArgumentName}' and source {att.ValueSource} to load an entity of type {parameter.ParameterType.FullNameInCode()} for parameter {parameter.Name}")
    {
    }
}

/// <summary>
/// Apply this on a 
/// </summary>
public class EntityAttribute : WolverineParameterAttribute
{
    public EntityAttribute()
    {
        ValueSource = ValueSource.InputMember;
    }

    public EntityAttribute(string argumentName) : base(argumentName)
    {
        ValueSource = ValueSource.InputMember;
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        if (!rules.TryFindPersistenceFrameProvider(container, parameter.ParameterType, out var provider))
        {
            throw new InvalidOperationException("Could not determine a matching persistence service for entity " +
                                                parameter.ParameterType.FullNameInCode());

        }

        // I know it's goofy that this refers to the saga, but it should work fine here too
        var idType = provider.DetermineSagaIdType(parameter.ParameterType, container);
        
        var identity = tryFindIdentityVariable(chain, parameter, idType);

        var frame = provider.DetermineLoadFrame(container, parameter.ParameterType, identity);

        var entity = frame.Creates.First(x => x.VariableType == parameter.ParameterType);

        // TODO -- what about returning null?

        return entity;
    }

    private Variable tryFindIdentityVariable(IChain chain, ParameterInfo parameter, Type idType)
    {
        if (ArgumentName.IsNotEmpty())
        {
            if (chain.TryFindVariable(ArgumentName, ValueSource, idType, out var variable))
            {
                return variable;
            }
        }
        
        if (chain.TryFindVariable(parameter.ParameterType.Name + "Id", ValueSource, idType, out var v2))
        {
            return v2;
        }
        
        if (chain.TryFindVariable("Id", ValueSource, idType, out var v3))
        {
            return v3;
        }

        throw new InvalidEntityLoadUsageException(this, parameter);
    }
}

public static class Storage
{
    public static Store<T> Store<T>(T entity) => new Store<T>(entity);
    public static Insert<T> Insert<T>(T entity) => new Insert<T>(entity);
    public static Update<T> Update<T>(T entity) => new Update<T>(entity);
    public static Delete<T> Delete<T>(T entity) => new Delete<T>(entity);
    public static Nothing<T> Nothing<T>() => new();

    internal static bool TryApply(Variable effect, GenerationRules rules, IServiceContainer container, IChain chain)
    {
        if (effect.VariableType.Closes(typeof(IStorageAction<>)) &&
            effect.VariableType.GetGenericTypeDefinition() == typeof(IStorageAction<>))
        {
            var entityType = effect.VariableType.GetGenericArguments()[0];
            if (rules.TryFindPersistenceFrameProvider(container, entityType, out var provider))
            {
                effect.UseReturnAction(v => provider.DetermineStorageActionFrame(entityType, effect).WrapIfNotNull(effect));
                provider.ApplyTransactionSupport(chain, container);
                return true;
            }

            throw new NoMatchingPersistenceProviderException(entityType);
        }

        return false;
    }
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

public record Nothing<T> : IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules, IServiceContainer container)
    {
        return new CommentFrame("Do nothing with the entity");
    }

    public StorageAction Action => StorageAction.Nothing;
    public T Entity => default!;
}


internal class EntityVariable : Variable
{
    public EntityVariable(Variable sideEffect) : base(sideEffect.VariableType.GetGenericArguments()[0], $"{sideEffect.Usage}.Entity")
    {
    }
}


public record Store<T>(T Entity) : ISideEffectAware, IStorageAction<T>
{
    public static Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        if (rules.TryFindPersistenceFrameProvider(container, typeof(T), out var provider))
        {
            provider.ApplyTransactionSupport(chain, container);
            var value = new EntityVariable(variable);
            return provider.DetermineStoreFrame(value, container).WrapIfNotNull(variable);
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
            provider.ApplyTransactionSupport(chain, container);
            var value = new EntityVariable(variable);
            return provider.DetermineDeleteFrame(value, container).WrapIfNotNull(variable);
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
            provider.ApplyTransactionSupport(chain, container);
            var value = new EntityVariable(variable);
            return provider.DetermineInsertFrame(value, container).WrapIfNotNull(variable);
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
            provider.ApplyTransactionSupport(chain, container);
            var value = new EntityVariable(variable);
            var frame = provider.DetermineUpdateFrame(value, container).WrapIfNotNull(variable);
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