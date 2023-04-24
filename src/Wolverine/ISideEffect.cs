using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine;

/// <summary>
/// Marker interface for a return value from a Wolverine
/// handler action. Any *public* Execute() or ExecuteAsync() method will be
/// called on this object
/// </summary>
public interface ISideEffect
{
    
}

internal class SideEffectPolicy : IChainPolicy
{
    public const string SyncMethod = "Execute";
    public const string AsyncMethod = "ExecuteAsync";
    
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains)
        {
            var sideEffects = chain.ReturnVariablesOfType<ISideEffect>();
            foreach (var effect in sideEffects)
            {
                var method = findMethod(effect.VariableType);
                if (method == null)
                    throw new InvalidSideEffectException(
                        $"Invalid Wolverine side effect exception for {effect.VariableType.FullNameInCode()}, no public {SyncMethod}/{AsyncMethod} method found");

                foreach (var parameter in method.GetParameters())
                {
                    chain.AddDependencyType(parameter.ParameterType);
                }

                effect.UseReturnAction(v =>
                {
                    return new MethodCall(effect.VariableType, method)
                    {
                        Target = effect,
                        CommentText = $"Placed by Wolverine's {nameof(ISideEffect)} policy"
                    };
                }, "Side Effect Policy");
            }
        }
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