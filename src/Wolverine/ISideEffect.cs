using System.Diagnostics.CodeAnalysis;
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
public interface ISideEffect : IWolverineReturnType, INotToBeRouted;


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

    // typeof(Applier<>).CloseAndBuildAs<IApplier>(effect.VariableType) closes the
    // internal Applier<T : ISideEffectAware> shape over a runtime-resolved side-
    // effect type so the generic static-virtual `BuildFrame` can be invoked.
    // Same chunk D / I / J / K pattern: side effect types are user-supplied via
    // handler return values and statically rooted by handler discovery; the
    // closure fires only at codegen time, not on the per-message dispatch path.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Applier<TSideEffect> closed over runtime side-effect type at codegen time; user-supplied types are statically rooted via handler return-type discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Applier<TSideEffect> closed over runtime side-effect type at codegen time; user-supplied types are statically rooted via handler return-type discovery. See AOT guide.")]
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

    /// <summary>
    ///     GH-4825. The side-effect return types of a chain, for the <c>codegen write</c> rooting block.
    /// </summary>
    internal static IEnumerable<Type> SideEffectTypesOf(IChain chain)
    {
        return chain.ReturnVariablesOfType(typeof(ISideEffect)).Select(x => x.VariableType);
    }

    /// <summary>
    ///     GH-4825. The types <see cref="findMethod" /> needs reflection metadata for, for one side-effect
    ///     type: the type itself, and each interface that declares <c>Execute</c> or <c>ExecuteAsync</c>.
    ///     <c>findMethod</c> walks the interfaces because a declared return type like <c>IStartStream</c>
    ///     only inherits <c>Execute</c> from <c>IMartenOp</c>, and an interface's <c>GetMethod</c> does not
    ///     see its base interfaces' members. The generated code calls <c>Execute</c> directly, but a direct
    ///     call does not keep the reflection metadata <c>GetMethod</c> needs, so without a root a native
    ///     image throws <see cref="InvalidSideEffectException" /> from <c>MapWolverineEndpoints</c>.
    ///     Member-less marker interfaces (<c>ISideEffect</c> itself) are left out: ILC rejects a
    ///     <c>[DynamicDependency]</c> that resolves no members (IL2037).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification =
            "Only reached from `codegen write`, which runs on CoreCLR behind DynamicCodeBuilder.WithinCodegenCommand. The types returned here are what that command emits as [DynamicDependency] roots, which is what keeps the metadata findMethod needs in a native image.")]
    internal static Type[] ReflectedTypesFor(Type sideEffectType)
    {
        var declaresExecute = sideEffectType.GetInterfaces().Where(x =>
            x.GetMethod(SyncMethod, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null ||
            x.GetMethod(AsyncMethod, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null);

        return [sideEffectType, ..declaresExecute];
    }

    private static void applySideEffectExecution(Variable effect, IChain chain)
    {
        if (effect.VariableType == typeof(ISideEffect))
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

    // GetMethod / GetInterfaces walk over a runtime-resolved ISideEffect
    // implementation type. ISideEffect is opt-in: user types implement
    // ISideEffect and are statically rooted via handler return-type discovery.
    // Mirrors the IResponse.findMethod suppression in the same chunk R.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "ISideEffect is opt-in; user-supplied side-effect types are statically rooted via handler return-type discovery. See AOT guide.")]
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