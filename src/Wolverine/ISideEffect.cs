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

/// <summary>
///     GH-4825. The types <see cref="SideEffectPolicy" /> reflects over when it applies a side effect, so
///     that <c>codegen write</c> can root their metadata for Native AOT.
/// </summary>
/// <remarks>
///     <see cref="SideEffectPolicy" /> is a policy, so it runs at startup in a native image too —
///     <see cref="TypeLoadMode.Static" /> and a committed pre-gen do not spare it. All it does there is
///     reflect: <c>findMethod</c> asks the side-effect type for a public <c>Execute</c>/<c>ExecuteAsync</c>,
///     and when ILC has trimmed that method's metadata the lookup answers null and the policy throws
///     <see cref="InvalidSideEffectException" /> while the chains are being built. The generated code
///     calling <c>Execute</c> directly is not enough: a call preserves the method, not the metadata a
///     <c>GetMethod</c> needs.
/// </remarks>
internal static class SideEffectAotRoots
{
    /// <summary>
    ///     Every type whose public methods <c>SideEffectPolicy.findMethod</c> walks for this chain.
    /// </summary>
    /// <remarks>
    ///     Answers nothing outside <c>codegen write</c>. The registry code files only emit a rooting block
    ///     when <see cref="DynamicCodeBuilder.WithinCodegenCommand" />, but <c>BuildFiles</c> is enumerated
    ///     during <see cref="TypeLoadMode.Static" /> attach as well — so without this guard a native image
    ///     would run the reflection below at startup to build a list it then throws away.
    /// </remarks>
    public static IEnumerable<Type> Of(IChain chain)
    {
        if (!DynamicCodeBuilder.WithinCodegenCommand) yield break;

        foreach (var effect in chain.ReturnVariablesOfType(typeof(ISideEffect)))
        {
            yield return effect.VariableType;

            foreach (var parent in declaringInterfacesOf(effect.VariableType))
            {
                yield return parent;
            }
        }
    }

    /// <summary>
    ///     The interfaces <c>SideEffectPolicy.findMethod</c> can find <c>Execute</c>/<c>ExecuteAsync</c> on
    ///     when the side-effect type itself declares neither — the normal case for a side effect declared AS
    ///     an interface: Wolverine.Marten's <c>IStartStream</c> inherits <c>Execute</c> from <c>IMartenOp</c>,
    ///     and that inherited declaration is the one the reported 6.46 failure could not find. Rooting the
    ///     return type alone keeps its interface list but not the methods the walk then looks for.
    /// </summary>
    /// <remarks>
    ///     GH-4840. The test is literally the one <c>findMethod</c> applies to each interface in the walk:
    ///     does it DECLARE a public <c>Execute</c> or <c>ExecuteAsync</c>. It used to be "is castable to
    ///     <see cref="ISideEffect" />", which is a proxy that happened to agree on <c>IStartStream</c> →
    ///     <c>IMartenOp</c> and does not in general. In one direction it rooted every marker interface
    ///     between the return type and the declaring one, none of which the walk needs. In the other it
    ///     missed an interface that declares <c>Execute</c> without itself inheriting <see cref="ISideEffect" />
    ///     — <c>findMethod</c> walks <em>every</em> interface of the return type, so that interface is where
    ///     it finds the method, and in a native image the trimmer had dropped exactly that metadata.
    ///     <para>
    ///     Its own method rather than an attribute on <see cref="Of" />, which is an iterator: a suppression
    ///     on the declaring method does not reach the compiler-generated MoveNext. <see cref="ISideEffect" />
    ///     itself and its base markers declare nothing, so this test leaves them out without naming them.
    ///     </para>
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification =
            "Only reached from `codegen write`, which runs on CoreCLR behind DynamicCodeBuilder.WithinCodegenCommand -- Of() returns empty otherwise. Emitting these roots is exactly what keeps a native image from needing this metadata at startup.")]
    private static IEnumerable<Type> declaringInterfacesOf(Type sideEffectType)
    {
        return sideEffectType.GetInterfaces().Where(declaresExecute);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Same codegen-only reach as declaringInterfacesOf, which is its only caller.")]
    private static bool declaresExecute(Type candidate)
    {
        const BindingFlags declaredPublicInstance =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // GetMethods rather than GetMethod: an interface overloading Execute would make GetMethod throw
        // AmbiguousMatchException, and the question here is only whether the name is declared at all.
        return candidate.GetMethods(declaredPublicInstance)
            .Any(x => x.Name is SideEffectPolicy.SyncMethod or SideEffectPolicy.AsyncMethod);
    }
}