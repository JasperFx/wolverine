using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Persistence;

/// <summary>
/// Emitted (via <c>IScopedContainerCreation.AddPostProcessor</c>) immediately after a handler's
/// service-location child scope is created. Primes that scope's <typeparamref name="THolder"/> with the
/// handler's outbox-enrolled <typeparamref name="TSession"/>, so any service-located session resolves to
/// that single enrolled session rather than a separate one. See GH-3001 and GH-4145.
/// </summary>
/// <remarks>
/// Registered by each store integration through <c>WolverineOptions.ScopingFrameSources</c>. The frame
/// self-guards: a chain that never created one of this store's sessions emits nothing, which is what
/// lets a host integrate several stores at once.
/// </remarks>
internal sealed class PrimeScopedSessionFrame<TSession, THolder> : SyncFrame, IUsesServiceProviderFrame
    where TSession : class
    where THolder : class, IScopedSessionHolder<TSession>
{
    private Variable? _session;
    private Variable? _scopedProvider;

    // The parent ScopedContainerCreation hands us the scoped IServiceProvider variable before we
    // resolve our other variables (avoiding a bi-directional dependency with the scope line).
    public void UseServiceProvider(Variable serviceProvider) => _scopedProvider = serviceProvider;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // The enrolled session the chain's own transactional frame created (NotServices: never the
        // container's own scoped session registration).
        _session = chain.TryFindVariable(typeof(TSession), VariableSource.NotServices);
        if (_session != null)
        {
            yield return _session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_session != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(THolder).FullNameInCode()}>({_scopedProvider!.Usage}).{nameof(IScopedSessionHolder<TSession>.Session)} = {_session.Usage};");
        }

        Next?.GenerateCode(method, writer);
    }

    // F#: mutable property assignment uses `<-` and no trailing semicolon.
    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_session != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FSharpName()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(THolder).FSharpName()}>({_scopedProvider!.Usage}).{nameof(IScopedSessionHolder<TSession>.Session)} <- {_session.Usage}");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
