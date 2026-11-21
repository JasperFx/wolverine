using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// Hook invoked for each envelope immediately before it is written to Wolverine's durable
/// (database-backed) dead-letter storage, from a buffered or a durable endpoint. Implementations may
/// mutate the envelope in place - for example re-serialize a redacted or encrypted body by editing
/// <see cref="Envelope.Message"/> and setting <see cref="Envelope.Data"/> to <c>null</c> so the store
/// re-serializes it - and/or return a replacement exception whose type and message are persisted in
/// place of the original.
/// </summary>
/// <remarks>
/// <para>
/// Interceptors are resolved from the application container and invoked in registration order; each
/// receives the exception returned by the previous one. Register them in DI, e.g.
/// <c>services.AddSingleton&lt;IDeadLetterInterceptor, MyInterceptor&gt;()</c>.
/// </para>
/// <para>
/// The distinctive use is scrubbing or replacing the <b>exception</b> recorded with a dead letter -
/// handlers sometimes interpolate sensitive data into exception messages, and the exception type and
/// message are stored outside the message serializer. Return a replacement exception (optionally one
/// implementing <see cref="IDeadLetterExceptionInfo"/> to keep the original type while redacting the
/// message) to control what is persisted.
/// </para>
/// <para>
/// The hook can also mutate the envelope body for dead-letter-only transforms: edit
/// <see cref="Envelope.Message"/> and set <see cref="Envelope.Data"/> to <c>null</c> so the store
/// re-serializes it.
/// </para>
/// <para>
/// Not invoked for broker-native dead-lettering, where the original payload bytes are handled by the
/// broker rather than persisted by Wolverine.
/// </para>
/// <para>
/// On a durable endpoint the replacement's type and message are also what the dead letter's
/// exception headers carry. Its failure group is still computed from the original exception.
/// </para>
/// <para>
/// Not invoked for an envelope whose <see cref="Envelope.Message"/> is null, such as one that could not be
/// deserialized: there is no message to redact, and its raw body is stored as it arrived.
/// </para>
/// <para>
/// On a durable endpoint, an interceptor that throws is treated like a dead-letter write that failed: nothing
/// is dead lettered, the envelope is scheduled to be executed again, and the original body is what runs again.
/// The unscrubbed envelope is never written to dead-letter storage.
/// </para>
/// </remarks>
public interface IDeadLetterInterceptor
{
    /// <param name="envelope">The failed envelope about to be stored. May be mutated in place.</param>
    /// <param name="exception">The exception associated with the failure, if any.</param>
    /// <param name="cancellation">The runtime cancellation token.</param>
    /// <returns>The exception to persist; return <paramref name="exception"/> to leave it unchanged.</returns>
    ValueTask<Exception?> BeforeStoreAsync(Envelope envelope, Exception? exception, CancellationToken cancellation);
}

internal static class DeadLetterInterception
{
    /// <summary>
    /// Runs every registered <see cref="IDeadLetterInterceptor"/> in registration order, each receiving the exception
    /// returned by the previous one
    /// </summary>
    /// <returns>The exception to persist with the dead letter</returns>
    internal static async ValueTask<Exception?> InterceptAsync(IWolverineRuntime runtime, Envelope envelope,
        Exception? exception)
    {
        // An envelope that never got as far as a message -- it could not be unwrapped or deserialized, or carries no
        // message type -- has nothing an interceptor can redact or re-serialize, and interceptors written against the
        // handler's message should not have to guard against a null one
        if (envelope.Message == null) return exception;

        foreach (var interceptor in runtime.Services.GetServices<IDeadLetterInterceptor>())
        {
            exception = await interceptor.BeforeStoreAsync(envelope, exception, runtime.Cancellation)
                .ConfigureAwait(false);
        }

        return exception;
    }
}
