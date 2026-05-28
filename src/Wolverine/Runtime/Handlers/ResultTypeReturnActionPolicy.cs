using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// GH-2221 — eager <see cref="IHandlerPolicy" /> that walks every <see cref="HandlerChain" /> at
/// <see cref="HandlerGraph.Compile" /> time and, for any chain whose handler return matches a
/// registered Result type, replaces the chain's default
/// <see cref="IReturnVariableActionSource" /> with <see cref="ResultUnwrappingActionSource" />.
///
/// Same Phase A vs Phase B pattern as <c>MartenStoreEagerPolicy</c> (GH-2944): the substitution
/// must happen before any chain's codegen runs so the unwrap frame ends up in the right place in
/// the generated source.
/// </summary>
internal sealed class ResultTypeReturnActionPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        if (!rules.Properties.TryGetValue(WolverineOptions.ResultTypeRegistryKey, out var raw)
            || raw is not ResultTypeRegistry registry
            || !registry.HasAny)
        {
            return;
        }

        foreach (var chain in chains)
        {
            ApplyTo(chain, registry);

            foreach (var byEndpoint in chain.ByEndpoint)
            {
                ApplyTo(byEndpoint, registry);
            }
        }
    }

    private static void ApplyTo(HandlerChain chain, ResultTypeRegistry registry)
    {
        if (chain.ReturnVariableActionSource is ResultUnwrappingActionSource) return;

        // The chain's handler can return Result<T> directly, Task<Result<T>>, or
        // ValueTask<Result<T>>. Inspect each handler call's actual return shape (skipping the
        // Task/ValueTask wrapper Wolverine already unwraps internally for await codegen) and
        // match against the registry.
        foreach (var call in chain.Handlers)
        {
            var returnType = UnwrapTaskLike(call.Method.ReturnType);
            if (returnType == null) continue;

            if (registry.IsResultType(returnType))
            {
                chain.ReturnVariableActionSource = new ResultUnwrappingActionSource();
                return;
            }
        }
    }

    private static Type? UnwrapTaskLike(Type returnType)
    {
        if (returnType == typeof(void) || returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return null;
        }

        if (returnType.IsGenericType)
        {
            var def = returnType.GetGenericTypeDefinition();
            if (def == typeof(Task<>) || def == typeof(ValueTask<>))
            {
                return returnType.GetGenericArguments()[0];
            }
        }

        return returnType;
    }
}
