using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// The Google Cloud Pub/Sub emulator from the gcloud CLI, running in a container. Its connection string is the
/// emulator's endpoint, <c>{host}:{port}</c> (the <c>PUBSUB_EMULATOR_HOST</c> format), which is what Wolverine's
/// <c>UsePubsubUsingNamedConnection(projectId, name)</c> reads.
/// </summary>
/// <param name="name">The name of the resource, which is also the name of its connection string</param>
/// <param name="projectId">The GCP project id the emulator starts with and referencing projects are told about</param>
public class PubsubEmulatorResource(string name, string projectId) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "http";
    internal const int EmulatorPort = 8085;

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// The emulator's endpoint, which serves both gRPC and REST
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new EndpointReference(this, PrimaryEndpointName);

    /// <summary>
    /// The emulator's host
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// The emulator's port
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// The GCP project id the emulator starts with. The emulator accepts any project id, so this is the one
    /// referencing projects are told to use, as <c>PUBSUB_PROJECT_ID</c>.
    /// </summary>
    public string ProjectId { get; } = projectId;

    /// <summary>
    /// The emulator's endpoint as <c>{host}:{port}</c>, the format of <c>PUBSUB_EMULATOR_HOST</c>
    /// </summary>
    public ReferenceExpression EmulatorHost =>
        ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

    /// <summary>
    /// The connection string is the emulator's endpoint, <c>{host}:{port}</c>
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => EmulatorHost;

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        // WithReference() hands each of these to a referencing project as {NAME}_{KEY}, e.g. PUBSUB_PROJECTID
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));
        yield return new("EmulatorHost", EmulatorHost);
        yield return new("ProjectId", ReferenceExpression.Create($"{ProjectId}"));
    }
}
