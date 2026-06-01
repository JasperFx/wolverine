namespace Wolverine.Runtime;

/// <summary>
/// Scope-local carrier for the active <see cref="MessageContext"/>. When a handler falls back to
/// service location, Wolverine's generated code creates a child <see cref="IServiceScope"/> off the
/// root provider; that scope does not share the per-message scope's <see cref="MessageContext"/>.
/// The codegen scoping frame primes this holder in the child scope immediately after it is created,
/// so any service-located <see cref="IMessageContext"/> / <see cref="IMessageBus"/> resolves to the
/// SAME context the handler received (enrolled with the active outbox) instead of a duplicate.
///
/// This replaces the AsyncLocal <c>MessageContext.Current</c> handoff (GH-2583) with a structural,
/// scope-local mechanism that does not depend on async-flow propagation.
/// </summary>
public sealed class ScopedMessageContextHolder
{
    public MessageContext? Context { get; set; }
}
