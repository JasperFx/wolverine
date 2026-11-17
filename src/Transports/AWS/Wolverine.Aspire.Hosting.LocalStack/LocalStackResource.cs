using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Wolverine.Aspire.Hosting;

/// <summary>
/// LocalStack, the AWS emulator, running SQS and SNS in a container. Its connection string is the emulator's
/// endpoint, <c>http://{host}:{port}</c>, which is what Wolverine's <c>UseAmazonSqsTransportUsingNamedConnection(name)</c>
/// and <c>UseAmazonSnsTransportUsingNamedConnection(name)</c> read.
/// </summary>
/// <param name="name">The name of the resource, which is also the name of its connection string</param>
/// <param name="region">The AWS region a referencing project's AWS SDK clients are told to use</param>
public class LocalStackResource(string name, string region) : ContainerResource(name), IResourceWithConnectionString
{
    internal const string PrimaryEndpointName = "http";
    internal const int GatewayPort = 4566;

    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// The LocalStack gateway, which serves every emulated AWS service
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new EndpointReference(this, PrimaryEndpointName);

    /// <summary>
    /// The gateway's host
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// The gateway's port
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// The AWS region a referencing project's AWS SDK clients are told to use. LocalStack accepts any region.
    /// </summary>
    public string Region { get; } = region;

    /// <summary>
    /// The connection string is the gateway's endpoint, <c>http://{host}:{port}</c>, the AWS SDK's <c>ServiceURL</c>
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.Url)}");

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        // WithReference() hands each of these to a referencing project as {NAME}_{KEY}, e.g. AWS_REGION for a
        // resource named "aws"
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));
        yield return new("Url", ConnectionStringExpression);
        yield return new("Region", ReferenceExpression.Create($"{Region}"));
    }
}
