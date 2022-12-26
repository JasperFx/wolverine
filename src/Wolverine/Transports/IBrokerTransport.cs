using Wolverine.Runtime;

namespace Wolverine.Transports;

public interface IBrokerTransport : ITransport
{
    /// <summary>
    ///     Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    bool AutoProvision { get; set; }

    /// <summary>
    ///     Should Wolverine attempt to purge all messages out of existing or discovered queues
    ///     on application start up? This can be useful for testing, and occasionally for ephemeral
    ///     messages
    /// </summary>
    bool AutoPurgeAllQueues { get; set; }

    /// <summary>
    ///     Optional prefix to append to all messaging object identifiers to make them unique when multiple developers
    ///     need to develop against a common message broker. I.e., sigh, you have to be using a cloud only tool.
    /// </summary>
    string? IdentifierPrefix { get; set; }

    ValueTask ConnectAsync(IWolverineRuntime runtime);

    /// <summary>
    ///     This helps to create a diagnostic table of broker state
    /// </summary>
    /// <returns></returns>
    IEnumerable<PropertyColumn> DiagnosticColumns();

    /// <summary>
    ///     Use to sanitize names for illegal characters
    /// </summary>
    /// <param name="identifier"></param>
    /// <returns></returns>
    string SanitizeIdentifier(string identifier);

    string MaybeCorrectName(string identifier);
}