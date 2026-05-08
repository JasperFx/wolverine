using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Splits a saga's incoming messages into <em>starting</em> and
/// <em>continuing</em> buckets the same way
/// <see cref="Wolverine.Configuration.Capabilities.ServiceCapabilities"/>
/// does. Shared so every <see cref="ISagaStoreDiagnostics"/>
/// implementation (Marten, EF Core, RavenDB, …) can return
/// <see cref="SagaTypeDescriptor"/> values whose message lists match
/// the host-wide capabilities snapshot byte-for-byte — important for
/// CritterWatch, which keys saga-graph rendering off both surfaces.
/// </summary>
/// <remarks>
/// Classification rule mirrors <see cref="SagaChain.DetermineFrames"/>:
/// <list type="bullet">
/// <item><c>Start</c> / <c>Starts</c> → starting only.</item>
/// <item><c>StartOrHandle</c> / <c>StartsOrHandles</c> → both starting and continuing
/// because at runtime the message can do either.</item>
/// <item><c>Orchestrate</c> / <c>Handle</c> / <c>Consume</c> (+plural variants) and
/// <c>NotFound</c> → continuing only.</item>
/// </list>
/// </remarks>
internal static class SagaMessageBuckets
{
    public static (List<TypeDescriptor> starting, List<TypeDescriptor> continuing) For(
        Type sagaType, HandlerGraph graph)
    {
        var starting = new List<TypeDescriptor>();
        var continuing = new List<TypeDescriptor>();

        foreach (var chain in saga_chains(graph).Where(c => c.SagaType == sagaType)
                     .OrderBy(c => c.MessageType.FullNameInCode()))
        {
            var role = Classify(chain);
            if (role is null) continue;

            var msgDesc = TypeDescriptor.For(chain.MessageType);
            switch (role.Value)
            {
                case Role.Start:
                    starting.Add(msgDesc);
                    break;
                case Role.StartOrHandle:
                    starting.Add(msgDesc);
                    continuing.Add(msgDesc);
                    break;
                case Role.Orchestrate:
                case Role.NotFound:
                    continuing.Add(msgDesc);
                    break;
            }
        }

        return (starting, continuing);
    }

    public enum Role
    {
        Start,
        StartOrHandle,
        Orchestrate,
        NotFound
    }

    public static Role? Classify(SagaChain chain)
    {
        var methodNames = chain.Handlers
            .Where(h => h.HandlerType.CanBeCastTo<Saga>())
            .Select(h => h.Method.Name)
            .Select(n => n.EndsWith("Async") ? n[..^"Async".Length] : n)
            .ToHashSet();

        if (methodNames.Contains(SagaChain.StartOrHandle) || methodNames.Contains(SagaChain.StartsOrHandles))
            return Role.StartOrHandle;

        if (methodNames.Contains(SagaChain.Start) || methodNames.Contains(SagaChain.Starts))
            return Role.Start;

        if (methodNames.Contains(SagaChain.Orchestrate) || methodNames.Contains(SagaChain.Orchestrates)
            || methodNames.Contains("Handle") || methodNames.Contains("Handles")
            || methodNames.Contains("Consume") || methodNames.Contains("Consumes"))
            return Role.Orchestrate;

        if (methodNames.Contains(SagaChain.NotFound))
            return Role.NotFound;

        return null;
    }

    /// <summary>
    /// Walks every saga chain on the handler graph, including per-endpoint
    /// inner chains created by MultipleHandlerBehavior.Separated.
    /// </summary>
    public static IEnumerable<SagaChain> saga_chains(HandlerGraph graph)
    {
        foreach (var chain in graph.Chains.OfType<SagaChain>())
        {
            yield return chain;
            foreach (var inner in chain.ByEndpoint.OfType<SagaChain>())
            {
                yield return inner;
            }
        }
    }
}
