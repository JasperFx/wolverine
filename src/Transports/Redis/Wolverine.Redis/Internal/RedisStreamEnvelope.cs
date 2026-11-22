namespace Wolverine.Redis.Internal;

/// <summary>
///     The envelope the stream listener builds for an entry it read, carrying the listener's own requeue state
/// </summary>
internal class RedisStreamEnvelope : Envelope
{
    /// <summary>
    ///     GH-5769. Set once a requeue's copy is on the stream, so a retry of the requeue after a failed
    ///     acknowledgement of the original does not add a second copy.
    /// </summary>
    public bool RequeueCopyAdded { get; set; }
}
