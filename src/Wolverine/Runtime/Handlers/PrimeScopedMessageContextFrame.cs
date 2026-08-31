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
        // Self-guarding, like the persistence-session frames: this is now attached to EVERY scope
        // Wolverine's codegen creates, and not all of them belong to a message handler or an HTTP
        // endpoint. A handler finds its MessageContext argument and an HTTP chain finds one through
        // MessageBusSource; anything else primes nothing rather than forcing a context into existence.
        //
        // NotServices, not Existing, and deliberately so: a variable source is a factory, so this DOES
        // build a MessageContext for a chain that had no other reason to name one. That is the right
        // answer here -- the context is the one the chain would use for any message it sends, and
        // priming the scope with it is the whole point of GH-3001. The persistence sessions in
        // PrimeScopedSessionFrame are the opposite case: manufacturing one invents a database
        // connection and an outbox enrolment that nothing in the chain will ever commit. See GH-4198.
        _context = chain.TryFindVariable(typeof(MessageContext), VariableSource.NotServices);
        if (_context != null)
        {
            yield return _context;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_context != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedMessageContextHolder).FullNameInCode()}>({_scopedProvider!.Usage}).{nameof(ScopedMessageContextHolder.Context)} = {_context.Usage};");
        }

        Next?.GenerateCode(method, writer);
    }

    // F#: mutable property assignment uses `<-` and no trailing semicolon.
    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_context != null)
        {
            writer.Write(
                $"{typeof(ServiceProviderServiceExtensions).FSharpName()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{typeof(ScopedMessageContextHolder).FSharpName()}>({_scopedProvider!.Usage}).{nameof(ScopedMessageContextHolder.Context)} <- {_context.Usage}");
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}
