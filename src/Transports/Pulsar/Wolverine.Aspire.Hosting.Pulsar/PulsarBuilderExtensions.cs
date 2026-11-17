using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// AppHost extensions for running an Apache Pulsar broker
/// </summary>
public static class PulsarBuilderExtensions
{
    /// <summary>
    /// The Pulsar image the resource runs, kept in step with Wolverine's own Pulsar test suite and docker-compose.yml
    /// </summary>
    public const string Image = "apachepulsar/pulsar";

    /// <summary>
    /// The default tag of <see cref="Image"/>
    /// </summary>
    public const string Tag = "4.2.4";

    /// <summary>
    /// Path of the broker health endpoint on the admin API, which answers 200 once the broker can serve topics
    /// </summary>
    public const string HealthCheckPath = "/admin/v2/brokers/health";

    /// <summary>
    /// Add an Apache Pulsar broker, run as <c>bin/pulsar standalone</c> in a container. Reference it from a project
    /// with <c>WithReference()</c> and that project gets the service URL, <c>pulsar://{host}:{port}</c>, as the
    /// connection string named <paramref name="name"/>, which is what Wolverine's
    /// <c>UsePulsarUsingNamedConnection(name)</c> reads. The admin API's URL comes along as
    /// <c>{NAME}_ADMINURL</c>. The resource is healthy, so <c>WaitFor()</c> lets a project start, once the broker's
    /// health endpoint answers.
    /// </summary>
    /// <param name="builder">The AppHost's builder</param>
    /// <param name="name">The name of the resource and of its connection string</param>
    /// <param name="port">The host port for the broker's binary protocol (6650 in the container); random when null</param>
    /// <param name="adminPort">The host port for the admin REST API (8080 in the container); random when null</param>
    /// <returns></returns>
    public static IResourceBuilder<PulsarResource> AddPulsar(this IDistributedApplicationBuilder builder,
        [ResourceName] string name, int? port = null, int? adminPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var pulsar = new PulsarResource(name);

        return builder.AddResource(pulsar)
            .WithImage(Image, Tag)
            .WithImageRegistry("docker.io")
            .WithArgs("bin/pulsar", "standalone")
            .WithEndpoint(port: port, targetPort: PulsarResource.BrokerPort, name: PulsarResource.BrokerEndpointName)
            .WithHttpEndpoint(port: adminPort, targetPort: PulsarResource.AdminPort, name: PulsarResource.AdminEndpointName)
            .WithHttpHealthCheck(HealthCheckPath, endpointName: PulsarResource.AdminEndpointName);
    }
}
