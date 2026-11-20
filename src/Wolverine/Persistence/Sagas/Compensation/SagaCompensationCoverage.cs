using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5672 / GH-5701. The concrete message types each saga's <c>Compensate</c> methods cover, worked out once from what
/// handler discovery found. Wolverine builds a <c>Fault&lt;T&gt;</c> chain for each of them, and the saga's description
/// and flow diagram read the same answer, so the picture and the runtime can't disagree.
/// </summary>
internal sealed class SagaCompensationCoverage
{
    public static readonly SagaCompensationCoverage Empty = new(new Dictionary<System.Reflection.MethodInfo, Type[]>());

    private readonly IReadOnlyDictionary<System.Reflection.MethodInfo, Type[]> _covered;

    private SagaCompensationCoverage(IReadOnlyDictionary<System.Reflection.MethodInfo, Type[]> covered)
    {
        _covered = covered;
    }

    /// <summary>
    /// The concrete message types <paramref name="method" /> compensates, beyond its own failed-message type when that
    /// is an interface, a base type or <c>object</c>: in name order
    /// </summary>
    public IReadOnlyList<Type> CoveredBy(CompensateMethod method)
        => _covered.TryGetValue(method.Method, out var types) ? types : [];

    /// <summary>
    /// Every concrete message type <paramref name="method" /> compensates, as a reader sees it: its own failed-message
    /// type when that is a concrete message, and every type it covers besides
    /// </summary>
    public IReadOnlyList<Type> ConcreteTypesFor(CompensateMethod method)
    {
        var types = new List<Type>();
        if (isConcrete(method.FailedType)) types.Add(method.FailedType);
        types.AddRange(CoveredBy(method).Where(x => !types.Contains(x)));

        return types.OrderBy(x => x.FullNameInCode(), StringComparer.Ordinal).ToArray();
    }

    private static bool isConcrete(Type type) => !type.IsInterface && !type.IsAbstract && type != typeof(object);

    public static SagaCompensationCoverage For(IReadOnlyList<HandlerCall> calls, IEnumerable<Type> registered)
    {
        var covered = new Dictionary<System.Reflection.MethodInfo, Type[]>();
        HashSet<Type>? known = null;

        foreach (var sagaType in calls.Select(x => x.HandlerType).Where(x => x.CanBeCastTo<Saga>()).Distinct())
        {
            var methods = SagaCompensationMethods.For(sagaType);
            if (methods.Count == 0) continue;

            var own = calls.Where(x => x.HandlerType == sagaType).ToArray();
            var handled = own.Select(x => x.MessageType).ToHashSet();
            var sends = SagaCompensations.SentBy(own, sagaType);

            foreach (var method in methods)
            {
                var types = new List<Type>();
                types.AddRange(method.NamedTypes);

                if (method.CoversAFamily)
                {
                    // A catch-all covers what this saga visibly sends, less what it sends as it completes. Anything else
                    // covers every message type the application knows that is assignable to it
                    var candidates = method.FailedType == typeof(object)
                        ? sends
                        : known ??= SagaCompensations.KnownMessageTypes(calls, registered);

                    types.AddRange(candidates.Where(method.Covers));
                }

                covered[method.Method] = types
                    .Distinct()
                    .Where(x => x != method.FailedType && SagaCompensations.IsCompensable(x, handled))
                    .OrderBy(x => x.FullNameInCode(), StringComparer.Ordinal)
                    .ToArray();
            }
        }

        return new SagaCompensationCoverage(covered);
    }
}
