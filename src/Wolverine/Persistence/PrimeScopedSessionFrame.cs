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
/// lets a host integrate several stores at once. That guard has to be a non-creating lookup, or it
/// manufactures the very thing it is testing for -- see the note in <see cref="FindVariables" /> and
/// GH-4198.
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
        // The enrolled session the chain's own transactional frame created -- and ONLY that. This has
        // to be VariableSource.Existing: All/NotServices run the registered IVariableSource
        // implementations, and a variable source is a factory, so asking either of those whether the
        // chain has a session MANUFACTURES one. Every store integration registers such a source (see
        // Wolverine.Marten's SessionVariableSource), so the creating form gave every chain that
        // service-locates anything an outbox-enrolled session it never asked for: opened, handed to
        // the holder below, never read and never committed -- and under sharded multi-tenancy, an
        // outright throw on a database the chain does not use. See GH-4198.
        _session = chain.TryFindVariable(typeof(TSession), VariableSource.Existing);
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
