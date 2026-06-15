namespace Wolverine.Configuration;

/// <summary>
/// A transport-agnostic declaration of where an endpoint's dead letters effectively go. Surfaced on
/// <see cref="Capabilities.EndpointDescriptor.DeadLetterStorage"/> so monitoring tools (for example
/// CritterWatch) can introspect each endpoint's dead-letter destination without transport-specific
/// knowledge — most importantly, to detect endpoints that dead-letter natively without being bridged
/// into Wolverine's durable, queryable storage.
/// </summary>
public enum DeadLetterStorageMode
{
    /// <summary>
    /// Dead letters are written to Wolverine's durable message store (the <c>wolverine_dead_letters</c>
    /// table), where they are queryable and replayable through <c>IDeadLetters</c>. This is the
    /// default for endpoints with no native broker dead letter queue.
    /// </summary>
    Durable,

    /// <summary>
    /// Dead letters are moved to a native broker dead letter queue and are <em>not</em> bridged into
    /// Wolverine's durable storage. These dead letters are invisible to tools that manage the durable
    /// dead letter queue — enabling native→durable recovery (see <c>EnableDeadLetterQueueRecovery()</c>)
    /// promotes this to <see cref="NativeWithRecovery"/>.
    /// </summary>
    Native,

    /// <summary>
    /// Dead letters are moved to a native broker dead letter queue <em>and</em> a background recovery
    /// listener copies them into Wolverine's durable storage, so they are both natively dead-lettered
    /// and queryable/replayable through <c>IDeadLetters</c>.
    /// </summary>
    NativeWithRecovery
}
