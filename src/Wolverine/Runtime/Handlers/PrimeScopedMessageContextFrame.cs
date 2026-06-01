using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// Emitted (via <c>IScopedContainerCreation.AddPostProcessor</c>) immediately after a handler's
/// service-location child scope is created. Primes that scope's <see cref="ScopedMessageContextHolder"/>
/// with the handler's <see cref="MessageContext"/>, so any service-located
/// <see cref="IMessageContext"/> / <see cref="IMessageBus"/> resolves to that single context (enrolled
/// with the active outbox) rather than a duplicate. See GH-3001.
/// </summary>
internal sealed class PrimeScopedMessageContextFrame : SyncFrame, IUsesServiceProviderFrame
{
    private Variable? _context;
    private Variable? _scopedProvider;

    // The parent ScopedContainerCreation hands us its scoped IServiceProvider variable BEFORE we
    // resolve our other variables, so we never ask the arranger for an IServiceProvider (which would
    // create a bi-directional dependency with the scope line that creates it).
    public void UseServiceProvider(Variable serviceProvider) => _scopedProvider = serviceProvider;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedMessageContextHolder).FullNameInCode()}>({_scopedProvider!.Usage}).{nameof(ScopedMessageContextHolder.Context)} = {_context!.Usage};");
        Next?.GenerateCode(method, writer);
    }
}
