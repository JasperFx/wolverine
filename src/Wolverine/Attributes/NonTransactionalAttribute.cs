namespace Wolverine.Attributes;

/// <summary>
///     Explicitly opts out a handler or HTTP endpoint from having transactional
///     middleware applied automatically by <c>AutoApplyTransactions()</c>.
/// </summary>
/// <remarks>
///     <para>
///     <b>That is its entire scope</b>, and the distinction matters. It suppresses the <em>automatic</em>
///     policy; it does not suppress a commit that some other feature owns as part of its own contract.
///     </para>
///     <para>
///     In particular it does <b>not</b> stop the event sourcing workflows — <c>[WriteModel]</c>,
///     <c>[DeciderFunction]</c>, <c>[DcbModel]</c> and their store-named spellings — from committing.
///     Those load a stream, hand you the state, and append whatever events you return; the commit is the
///     back half of that bargain, not a policy layered on top. Suppressing it would silently discard the
///     events the handler just produced, which is the one outcome worth ruling out. A chain in that shape
///     therefore still reports <c>IsTransactional = true</c>, because its generated code does in fact end
///     in a commit. GH-3911.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class NonTransactionalAttribute : Attribute;
