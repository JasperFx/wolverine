using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// AppHost extensions for running LocalStack with the SQS and SNS services
/// </summary>
public static class LocalStackBuilderExtensions
{
    /// <summary>
    /// The LocalStack image the resource runs, kept in step with Wolverine's own docker-compose.yml
    /// </summary>
    public const string Image = "localstack/localstack";

    /// <summary>
    /// The default tag of <see cref="Image"/>
    /// </summary>
    public const string Tag = "4";

    /// <summary>
    /// The region a referencing project is told to use unless you pass one to <c>AddLocalStack()</c>
    /// </summary>
    public const string DefaultRegion = "us-east-1";

    /// <summary>
    /// Path of LocalStack's own health endpoint
    /// </summary>
    public const string HealthCheckPath = "/_localstack/health";

    /// <summary>
    /// The placeholder credentials a referencing project's AWS SDK clients are given. LocalStack accepts anything.
    /// </summary>
    public const string PlaceholderCredential = "test";

    /// <summary>
    /// Add LocalStack, the AWS emulator, running SQS and SNS in a container. Reference it from a project with
    /// <c>WithReference()</c> and that project gets:
    /// <list type="bullet">
    /// <item>the emulator's endpoint, <c>http://{host}:{port}</c>, as the connection string named <paramref name="name"/>,
    /// which is what Wolverine's <c>UseAmazonSqsTransportUsingNamedConnection(name)</c> and
    /// <c>UseAmazonSnsTransportUsingNamedConnection(name)</c> read</item>
    /// <item><c>AWS_ENDPOINT_URL</c>, <c>AWS_REGION</c>, <c>AWS_DEFAULT_REGION</c>, <c>AWS_ACCESS_KEY_ID</c> and
    /// <c>AWS_SECRET_ACCESS_KEY</c>, so every AWS SDK client the project builds for itself talks to LocalStack too</item>
    /// </list>
    /// The resource is healthy, so <c>WaitFor()</c> lets a project start, once LocalStack's health endpoint answers.
    /// </summary>
    /// <param name="builder">The AppHost's builder</param>
    /// <param name="name">The name of the resource and of its connection string</param>
    /// <param name="port">The host port for the LocalStack gateway (4566 in the container); random when null</param>
    /// <param name="region">The AWS region a referencing project's AWS SDK clients are told to use</param>
    /// <returns></returns>
    public static IResourceBuilder<LocalStackResource> AddLocalStack(this IDistributedApplicationBuilder builder,
        [ResourceName] string name, int? port = null, string region = DefaultRegion)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);

        var localStack = new LocalStackResource(name, region);

        return builder.AddResource(localStack)
            .WithImage(Image, Tag)
            .WithImageRegistry("docker.io")
            // Only what Wolverine's SQS and SNS transports use, started eagerly so the first call from a
            // project does not pay for it
            .WithEnvironment("SERVICES", "sqs,sns")
            .WithEnvironment("EAGER_SERVICE_LOADING", "1")
            .WithHttpEndpoint(port: port, targetPort: LocalStackResource.GatewayPort, name: LocalStackResource.PrimaryEndpointName)
            .WithHttpHealthCheck(HealthCheckPath, endpointName: LocalStackResource.PrimaryEndpointName);
    }

    /// <summary>
    /// Reference LocalStack from a project: the emulator's endpoint as the connection string named
    /// <paramref name="connectionName"/> (the resource's name by default), which Wolverine's
    /// <c>UseAmazonSqsTransportUsingNamedConnection(name)</c> and <c>UseAmazonSnsTransportUsingNamedConnection(name)</c>
    /// read, plus <c>AWS_ENDPOINT_URL</c>, <c>AWS_REGION</c>, <c>AWS_DEFAULT_REGION</c> and placeholder
    /// <c>AWS_ACCESS_KEY_ID</c> / <c>AWS_SECRET_ACCESS_KEY</c>, which the AWS SDK reads for any client the project
    /// builds itself.
    /// </summary>
    /// <param name="builder">The project, or any other resource with an environment</param>
    /// <param name="localStack">The LocalStack resource</param>
    /// <param name="connectionName">An override of the connection string's name</param>
    /// <typeparam name="TDestination"></typeparam>
    /// <returns></returns>
    public static IResourceBuilder<TDestination> WithReference<TDestination>(this IResourceBuilder<TDestination> builder,
        IResourceBuilder<LocalStackResource> localStack, string? connectionName = null)
        where TDestination : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(localStack);

        builder.WithReference((IResourceBuilder<IResourceWithConnectionString>)localStack, connectionName);

        var resource = localStack.Resource;
        return builder.WithEnvironment(context =>
        {
            context.EnvironmentVariables["AWS_ENDPOINT_URL"] = resource.ConnectionStringExpression;
            context.EnvironmentVariables["AWS_REGION"] = resource.Region;
            context.EnvironmentVariables["AWS_DEFAULT_REGION"] = resource.Region;
            context.EnvironmentVariables["AWS_ACCESS_KEY_ID"] = PlaceholderCredential;
            context.EnvironmentVariables["AWS_SECRET_ACCESS_KEY"] = PlaceholderCredential;
        });
    }
}
