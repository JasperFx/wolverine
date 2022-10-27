using System.Collections.Generic;
using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public interface IBrokerTransport : ITransport
{
    /// <summary>
    /// Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    bool AutoProvision { get; set; }

    /// <summary>
    /// Should Wolverine attempt to purge all messages out of existing or discovered queues
    /// on application start up? This can be useful for testing, and occasionally for ephemeral
    /// messages
    /// </summary>
    bool AutoPurgeAllQueues { get; set; }

    ValueTask ConnectAsync(IWolverineRuntime logger);

    /// <summary>
    /// This helps to create a diagnostic table of broker state
    /// </summary>
    /// <returns></returns>
    IEnumerable<PropertyColumn> DiagnosticColumns();
}