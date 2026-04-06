using Wolverine.Transports;

namespace Wolverine.MQTT.Internals;

internal class MqttHealthCheck : WolverineTransportHealthCheck
{
    private readonly MqttTransport _transport;

    public MqttHealthCheck(MqttTransport transport) => _transport = transport;

    public override string TransportName => "MQTT";
    public override string Protocol => "mqtt";

    public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var status = TransportHealthStatus.Healthy;
        string? message = null;
        var data = new Dictionary<string, object>();

        try
        {
            var client = _transport.Client;
            data["IsConnected"] = client.IsConnected;

            if (!client.IsConnected)
            {
                status = TransportHealthStatus.Unhealthy;
                message = "MQTT client is not connected";
            }
        }
        catch
        {
            status = TransportHealthStatus.Degraded;
            message = "MQTT transport not yet initialized";
        }

        // MQTT is pub/sub — no broker-side queue depth concept
        return Task.FromResult(new TransportHealthResult(TransportName, Protocol, status, message,
            DateTimeOffset.UtcNow, data));
    }
}
