using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.ComplianceTests;

public static class ConnectionStateTestHelpers
{
    /// <summary>
    /// Poll the endpoint health snapshots until the listening endpoint with the given URI scheme reports the
    /// expected <see cref="TransportConnectionState"/>, or the timeout elapses. Returns the last observed state.
    /// Brokers connect asynchronously at startup (and Pulsar's consumer transitions Disconnected -> Active), so a
    /// poll is more robust than a single read.
    /// </summary>
    public static async Task<TransportConnectionState> WaitForListenerConnectionStateAsync(
        IHost host, string scheme, TransportConnectionState expected, int timeoutMilliseconds = 15000)
    {
        var runtime = host.GetRuntime();
        var stopwatch = Stopwatch.StartNew();
        var last = TransportConnectionState.Unknown;

        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            var snapshot = runtime.Endpoints.CollectEndpointHealth()
                .FirstOrDefault(s => s.Direction == EndpointDirection.Listening && s.Uri.Scheme == scheme);

            if (snapshot != null)
            {
                last = snapshot.ConnectionState;
                if (last == expected)
                {
                    return last;
                }
            }

            await Task.Delay(100);
        }

        return last;
    }
}
