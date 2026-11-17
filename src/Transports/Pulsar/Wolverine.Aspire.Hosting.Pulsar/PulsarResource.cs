using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// An Apache Pulsar broker running as <c>bin/pulsar standalone</c> in a container. Its connection string is the
/// broker's service URL, <c>pulsar://{host}:{port}</c>, which is what Wolverine's
/// <c>UsePulsarUsingNamedConnection(name)</c> reads.
/// </summary>
/// <param name="name">The name of the resource, which is also the name of its connection string</param>
public class PulsarResource(string name) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string BrokerEndpointName = "tcp";
    internal const string AdminEndpointName = "http";

    internal const int BrokerPort = 6650;
    internal const int AdminPort = 8080;

    private EndpointReference? _brokerEndpoint;
    private EndpointReference? _adminEndpoint;

    /// <summary>
    /// The broker's binary protocol endpoint, the one Pulsar clients connect to
    /// </summary>
    public EndpointReference BrokerEndpoint => _brokerEndpoint ??= new EndpointReference(this, BrokerEndpointName);

    /// <summary>
    /// The broker's HTTP endpoint, which serves the admin REST API (and <c>pulsar-admin</c>)
    /// </summary>
    public EndpointReference AdminEndpoint => _adminEndpoint ??= new EndpointReference(this, AdminEndpointName);

    /// <summary>
    /// The broker's host
    /// </summary>
    public EndpointReferenceExpression Host => BrokerEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// The broker's port
    /// </summary>
    public EndpointReferenceExpression Port => BrokerEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// The broker's service URL, <c>pulsar://{host}:{port}</c>
    /// </summary>
    public ReferenceExpression ServiceUrl =>
        ReferenceExpression.Create($"pulsar://{BrokerEndpoint.Property(EndpointProperty.HostAndPort)}");

    /// <summary>
    /// The URL of the admin REST API, <c>http://{host}:{port}</c>
    /// </summary>
    public ReferenceExpression AdminUrl =>
        ReferenceExpression.Create($"{AdminEndpoint.Property(EndpointProperty.Url)}");

    /// <summary>
    /// The connection string is the broker's service URL, <c>pulsar://{host}:{port}</c>
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => ServiceUrl;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        // WithReference() hands each of these to a referencing project as {NAME}_{KEY}, e.g. PULSAR_ADMINURL
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));
        yield return new("ServiceUrl", ServiceUrl);
        yield return new("AdminUrl", AdminUrl);
    }
}
