using System.Collections.Concurrent;

namespace Wolverine.Configuration;

/// <summary>
/// Central catalog of every <see cref="IResultTypeRegistration" /> the host knows about. Consulted
/// from the codegen seams (<c>ResultTypeContinuationPolicy</c>, <c>ResultUnwrappingActionSource</c>)
/// and from the runtime caller-side <c>InvokeAsync&lt;T&gt;</c> unwrap. See GH-2221.
///
/// The runtime hot path is <see cref="TryFind" />, which resolves a concrete handler-return type
/// (always closed) against the registered set, including open-generic registrations. Resolutions
/// are memoized on a copy-on-write <see cref="ConcurrentDictionary{TKey,TValue}" /> so repeat
/// dispatches don't re-walk the list.
/// </summary>
public sealed class ResultTypeRegistry
{
    private readonly List<IResultTypeRegistration> _registrations = new();
    private readonly ConcurrentDictionary<Type, IResultTypeRegistration?> _resolutionCache = new();

    /// <summary>The registered set, in insertion order. Reads are non-locking — the list is only
    /// mutated through <see cref="Add" /> at bootstrap.</summary>
    public IReadOnlyList<IResultTypeRegistration> Registrations => _registrations;

    /// <summary>True when at least one Result type has been registered. Cheap pre-check used by
    /// the codegen seams to short-circuit when the feature isn't in play.</summary>
    public bool HasAny => _registrations.Count > 0;

    public void Add(IResultTypeRegistration registration)
    {
        if (registration is null) throw new ArgumentNullException(nameof(registration));

        // Replace any prior registration for the same key type so re-bootstrapping (tests,
        // hot-reload) is well-defined.
        for (var i = 0; i < _registrations.Count; i++)
        {
            if (_registrations[i].ResultType == registration.ResultType)
            {
                _registrations[i] = registration;
                _resolutionCache.Clear();
                return;
            }
        }

        _registrations.Add(registration);
        _resolutionCache.Clear();
    }

    /// <summary>
    /// Resolve a concrete (closed) type against the registered set. Returns the first registration
    /// that <see cref="IResultTypeRegistration.Matches" /> the candidate, or null if none does.
    /// Cached per <paramref name="candidate" /> for hot-path use.
    /// </summary>
    public IResultTypeRegistration? TryFind(Type candidate)
    {
        if (candidate is null) return null;
        return _resolutionCache.GetOrAdd(candidate, ResolveUncached);
    }

    /// <summary>True when <paramref name="candidate" /> matches a registered Result type.</summary>
    public bool IsResultType(Type candidate) => TryFind(candidate) != null;

    private IResultTypeRegistration? ResolveUncached(Type candidate)
    {
        // Prefer exact matches over open-generic matches so a user-registered closed type wins
        // over an open-generic catch-all (e.g. registering Result<OrderPlaced> specifically can
        // override behaviour for that closed form even when Result<> is also registered).
        for (var i = 0; i < _registrations.Count; i++)
        {
            var reg = _registrations[i];
            if (!reg.IsOpenGeneric && reg.ResultType == candidate) return reg;
        }

        for (var i = 0; i < _registrations.Count; i++)
        {
            var reg = _registrations[i];
            if (reg.IsOpenGeneric && reg.Matches(candidate)) return reg;
        }

        return null;
    }
}
