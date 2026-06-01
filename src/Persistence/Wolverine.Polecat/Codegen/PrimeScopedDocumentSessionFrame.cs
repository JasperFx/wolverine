using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Polecat;

namespace Wolverine.Polecat.Codegen;

/// <summary>
/// Emitted (via <c>IScopedContainerCreation.AddPostProcessor</c>) immediately after a handler's
/// service-location child scope is created. Primes that scope's <see cref="ScopedDocumentSessionHolder"/>
/// with the handler's outbox-enrolled <see cref="IDocumentSession"/>, so any service-located
/// <see cref="IDocumentSession"/> / <see cref="IQuerySession"/> resolves to that single enrolled
/// session rather than a separate one. See GH-3001.
/// </summary>
internal sealed class PrimeScopedDocumentSessionFrame : SyncFrame, IUsesServiceProviderFrame
{
    private Variable? _session;
    private Variable? _scopedProvider;

    // The parent ScopedContainerCreation hands us the scoped IServiceProvider variable before we
    // resolve our other variables (avoiding a bi-directional dependency with the scope line).
    public void UseServiceProvider(Variable serviceProvider) => _scopedProvider = serviceProvider;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // The enrolled session created by CreateDocumentSessionFrame (NotServices: never the
        // container's own scoped IDocumentSession registration).
        _session = chain.TryFindVariable(typeof(IDocumentSession), VariableSource.NotServices);
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
                $"{typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedDocumentSessionHolder).FullNameInCode()}>({_scopedProvider!.Usage}).{nameof(ScopedDocumentSessionHolder.Session)} = {_session.Usage};");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_session != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FSharpName()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedDocumentSessionHolder).FSharpName()}>({_scopedProvider!.Usage}).{nameof(ScopedDocumentSessionHolder.Session)} <- {_session.Usage}");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
