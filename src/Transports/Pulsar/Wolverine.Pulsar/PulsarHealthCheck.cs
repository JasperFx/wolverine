using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal class PulsarHealthCheck : WolverineTransportHealthCheck
{
    private readonly PulsarTransport _transport;

    public PulsarHealthCheck(PulsarTransport transport) => _transport = transport;

    public override string TransportName => "Pulsar";
    public override string Protocol => "pulsar";

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        var client = _transport.Client;
        data["HasClient"] = client != null;

        if (client == null)
        {
            status = TransportHealthStatus.Degraded;
            message = "Pulsar client not yet initialized";
        }

        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data));
    }
}
