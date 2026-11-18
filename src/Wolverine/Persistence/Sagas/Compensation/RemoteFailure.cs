// GH-5556 SPEC STUB: the shape Jeremy settled on 2026-11-17. Specified by Bobcat specifications first; nothing in
// Wolverine builds or matches it yet.

// ReSharper disable once CheckNamespace
namespace Wolverine;

/// <summary>
/// GH-5556. A <typeparamref name="TException" />, or an exception derived from it, thrown in another service. Only an
/// <see cref="ExceptionInfo" /> crosses the wire, so a saga that compensates a step which failed somewhere else takes
/// this in place of the live exception: <c>Compensate(ReserveInventory failed, RemoteFailure&lt;OutOfStockException&gt; failure)</c>.
/// Wolverine matches it through the exception's type hierarchy (<see cref="ExceptionInfo.Is{TException}" />, GH-5555),
/// so a subclass thrown in the other service matches too.
/// </summary>
/// <remarks>
/// <para>
/// A live exception parameter (<c>Compensate(PaymentReceived failed, LedgerUnavailableException ex)</c>) is only for a
/// failure in the saga's own service. <c>Compensate(T failed, ExceptionInfo ex)</c> is the catch-all for any other
/// failure of <c>T</c>, local or remote.
/// </para>
/// <para>SPEC STUB (GH-5556): specified by Bobcat specifications first; Wolverine does not build or match it yet.</para>
/// </remarks>
/// <param name="Exception">What the other service captured of the exception</param>
public sealed record RemoteFailure<TException>(ExceptionInfo Exception) where TException : Exception
{
    /// <summary>The exception's message, as the other service captured it. Empty when that service redacts messages.</summary>
    public string Message => Exception.Message;

    /// <summary>The full name of the exception type that was actually thrown: <typeparamref name="TException" /> or a subclass.</summary>
    public string Type => Exception.Type;
}
