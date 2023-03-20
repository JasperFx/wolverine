using Wolverine.Transports;

namespace Wolverine.Transports.Postgresql.Internal;

public interface IPostgresListeningEndpoint
{
    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumConcurrentMessages { get; }
}
