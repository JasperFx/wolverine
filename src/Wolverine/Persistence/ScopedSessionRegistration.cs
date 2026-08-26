using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Persistence;

/// <summary>
/// GH-3001 / GH-4145. The registration half of the service-location session priming that every document
/// store integration applies: make a scoped session resolution prefer the session
/// <see cref="PrimeScopedSessionFrame{TSession,THolder}"/> put in the scope's
/// <see cref="IScopedSessionHolder{TSession}"/>, and fall back to the store's own session factory
/// everywhere else.
/// </summary>
internal static class ScopedSessionRegistration
{
    /// <summary>
    /// Replace the existing scoped registration for <typeparamref name="T"/> with one that prefers the
    /// scope-primed session — the outbox-enrolled session the handler is using — and otherwise defers to
    /// whatever was registered before. Preserving the original factory keeps the store's exact
    /// session-building (options, tenancy) for the fall-back path.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="findPrimed">Reads the current scope's holder. Returns null outside a handler scope.</param>
    /// <param name="fallbackWhenUnregistered">
    /// Used when nothing has registered <typeparamref name="T"/> at all, which is the RavenDb case —
    /// Wolverine owns that registration outright rather than decorating one. Leave null to make this a
    /// pure decoration that no-ops when there is nothing to decorate.
    /// </param>
    internal static void PreferPrimedSession<T>(this IServiceCollection services,
        Func<IServiceProvider, object?> findPrimed,
        Func<IServiceProvider, T>? fallbackWhenUnregistered = null) where T : class
    {
        Func<IServiceProvider, T> original;

        // Skip keyed descriptors: their ImplementationFactory / ImplementationInstance getters throw,
        // and a keyed registration is not what an un-keyed resolution would have picked up anyway.
        var descriptor = services.LastOrDefault(x => x.ServiceType == typeof(T) && !x.IsKeyedService);

        if (descriptor == null)
        {
            if (fallbackWhenUnregistered == null)
            {
                return;
            }

            original = fallbackWhenUnregistered;
        }
        else if (descriptor.ImplementationFactory != null)
        {
            var factory = descriptor.ImplementationFactory;
            services.Remove(descriptor);
            original = sp => (T)factory(sp);
        }
        else if (descriptor.ImplementationInstance is T instance)
        {
            services.Remove(descriptor);
            original = _ => instance;
        }
        else if (descriptor.ImplementationType != null)
        {
            var implementationType = descriptor.ImplementationType;
            services.Remove(descriptor);
            original = sp => (T)ActivatorUtilities.CreateInstance(sp, implementationType);
        }
        else
        {
            // Nothing we can reproduce. Leave the original registration alone rather than replacing it
            // with something that cannot build the same session.
            return;
        }

        services.AddScoped<T>(sp => findPrimed(sp) is T primed ? primed : original(sp));
    }
}
