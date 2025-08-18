using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine;

/// <summary>
///     Marker interface for a return value from a Wolverine
///     handler action. Any *public* Execute() or ExecuteAsync() method will be
///     called on this object
/// </summary>
public interface ISideEffect : IWolverineReturnType;


/// <summary>
/// Static interface that marks a return type that "knows" how to do extra
/// code generation to handle a side effect
/// </summary>
public interface ISideEffectAware : ISideEffect
{
    static abstract Frame BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container);
}

internal class SideEffectPolicy : IChainPolicy
{
    public const string SyncMethod = "Execute";
    public const string AsyncMethod = "ExecuteAsync";

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            lookForSingularSideEffects(rules, container, chain);
        }
    }

    private static void lookForSingularSideEffects(GenerationRules rules, IServiceContainer container, IChain chain)
    {
        var sideEffects = chain.ReturnVariablesOfType<ISideEffect>();
        foreach (var effect in sideEffects.ToArray())
        {
            if (effect.VariableType.CanBeCastTo(typeof(ISideEffectAware)))
            {
                if (!Storage.TryApply(effect, rules, container, chain))
                {
                    var applier = typeof(Applier<>).CloseAndBuildAs<IApplier>(effect.VariableType);
                    var frame = applier.Apply(chain, effect, rules, container);
                    effect.UseReturnAction(v => frame);
                }
            }
            else
            {
                applySideEffectExecution(effect, chain);
            }

        }
    }

    internal interface IApplier
    {
        Frame Apply(IChain chain, Variable variable, GenerationRules rules,
            IServiceContainer container);
    }

    internal class Applier<T> : IApplier where T : ISideEffectAware
    {
        public Frame Apply(IChain chain, Variable variable, GenerationRules rules,
            IServiceContainer container)
        {
            return T.BuildFrame(chain, variable, rules, container);
        }
    }

    private static void applySideEffectExecution(Variable effect, IChain chain)
    {
        if (effect.GetType() == typeof(ISideEffect))
        {
            throw new InvalidOperationException($"Return the concrete type of ISideEffect so that Wolverine can 'know' how to call into your side effect and not ISideEffect itself");
        }
        
        var method = findMethod(effect.VariableType);
        if (method == null)
        {
            throw new InvalidSideEffectException(
                $"Invalid Wolverine side effect exception for {effect.VariableType.FullNameInCode()}, no public {SyncMethod}/{AsyncMethod} method found");
        }

        foreach (var parameter in method.GetParameters()) chain.AddDependencyType(parameter.ParameterType);

        effect.UseReturnAction(_ =>
        {
            return new IfElseNullGuardFrame.IfNullGuardFrame(
                effect,
                new MethodCall(effect.VariableType, method)
                {
                    Target = effect,
                    CommentText = $"Placed by Wolverine's {nameof(ISideEffect)} policy"
                });
        }, "Side Effect Policy");
    }

    private static MethodInfo? findMethod(Type effectType)
    {
        return
            effectType.GetMethod(SyncMethod,
                BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance)
            ?? effectType.GetMethod(AsyncMethod,
                BindingFlags.Public | BindingFlags.FlattenHierarchy | BindingFlags.Instance)
            ?? effectType.GetInterfaces().FirstValue(findMethod);
    }
}

public class InvalidSideEffectException : Exception
{
    public InvalidSideEffectException(string? message) : base(message)
    {
    }
}