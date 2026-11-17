using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// AppHost extensions for running the Google Cloud Pub/Sub emulator
/// </summary>
public static class PubsubEmulatorBuilderExtensions
{
    /// <summary>
    /// The gcloud CLI image with the emulators in it, kept in step with Wolverine's own docker-compose.yml
    /// </summary>
    public const string Image = "google.com/cloudsdktool/google-cloud-cli";

    /// <summary>
    /// The registry <see cref="Image"/> comes from
    /// </summary>
    public const string Registry = "gcr.io";

    /// <summary>
    /// The default tag of <see cref="Image"/>
    /// </summary>
    public const string Tag = "emulators";

    /// <summary>
    /// The project id the emulator starts with unless you pass one to <c>AddPubsubEmulator()</c>
    /// </summary>
    public const string DefaultProjectId = "wolverine";

    /// <summary>
    /// Add the Google Cloud Pub/Sub emulator from the gcloud CLI, running in a container. Reference it from a project
    /// with <c>WithReference()</c> and that project gets:
    /// <list type="bullet">
    /// <item>the emulator's endpoint, <c>{host}:{port}</c>, as the connection string named <paramref name="name"/>,
    /// which is what Wolverine's <c>UsePubsubUsingNamedConnection(projectId, name)</c> reads</item>
    /// <item><c>PUBSUB_EMULATOR_HOST</c> and <c>PUBSUB_PROJECT_ID</c>, what <c>gcloud beta emulators pubsub env-init</c>
    /// sets, for any Google client the project builds itself</item>
    /// </list>
    /// The resource is healthy, so <c>WaitFor()</c> lets a project start, once the emulator answers HTTP.
    /// </summary>
    /// <param name="builder">The AppHost's builder</param>
    /// <param name="name">The name of the resource and of its connection string</param>
    /// <param name="projectId">The GCP project id the emulator starts with and referencing projects are told about.
    /// Pass the same one to <c>UsePubsubUsingNamedConnection()</c> in the service.</param>
    /// <param name="port">The host port for the emulator (8085 in the container); random when null</param>
    /// <returns></returns>
    public static IResourceBuilder<PubsubEmulatorResource> AddPubsubEmulator(this IDistributedApplicationBuilder builder,
        [ResourceName] string name, string projectId = DefaultProjectId, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var emulator = new PubsubEmulatorResource(name, projectId);

        return builder.AddResource(emulator)
            .WithImage(Image, Tag)
            .WithImageRegistry(Registry)
            .WithArgs("gcloud", "--quiet", "beta", "emulators", "pubsub", "start",
                $"--host-port=0.0.0.0:{PubsubEmulatorResource.EmulatorPort}", $"--project={projectId}")
            .WithHttpEndpoint(port: port, targetPort: PubsubEmulatorResource.EmulatorPort, name: PubsubEmulatorResource.PrimaryEndpointName)
            // The emulator answers "Ok" on its root once it is serving
            .WithHttpHealthCheck("/", endpointName: PubsubEmulatorResource.PrimaryEndpointName);
    }

    /// <summary>
    /// Reference the Pub/Sub emulator from a project: the emulator's endpoint as the connection string named
    /// <paramref name="connectionName"/> (the resource's name by default), which Wolverine's
    /// <c>UsePubsubUsingNamedConnection(projectId, name)</c> reads, plus <c>PUBSUB_EMULATOR_HOST</c> and
    /// <c>PUBSUB_PROJECT_ID</c> for any Google client the project builds itself.
    /// </summary>
    /// <param name="builder">The project, or any other resource with an environment</param>
    /// <param name="emulator">The Pub/Sub emulator resource</param>
    /// <param name="connectionName">An override of the connection string's name</param>
    /// <typeparam name="TDestination"></typeparam>
    /// <returns></returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder,
        IResourceBuilder<PubsubEmulatorResource> emulator, string? connectionName = null)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(emulator);

        builder.WithReference((IResourceBuilder<IResourceWithConnectionString>)emulator, connectionName);

        var resource = emulator.Resource;
        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables["PUBSUB_EMULATOR_HOST"] = resource.EmulatorHost;
            context.EnvironmentVariables["PUBSUB_PROJECT_ID"] = resource.ProjectId;
        });
    }
}
