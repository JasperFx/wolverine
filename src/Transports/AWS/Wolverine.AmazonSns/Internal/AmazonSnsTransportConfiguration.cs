using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSns.Internal;

public class AmazonSnsTransportConfiguration : BrokerExpression<AmazonSnsTransport, AmazonSnsTopic, AmazonSnsTopic,
    AmazonSnsListenerConfiguration, AmazonSnsSubscriberConfiguration, AmazonSnsTransportConfiguration>
{
    public AmazonSnsTransportConfiguration(AmazonSnsTransport transport, WolverineOptions options) : base(transport,
        options)
    {
    }

    protected override AmazonSnsListenerConfiguration createListenerExpression(AmazonSnsTopic listenerEndpoint)
    {
        return new AmazonSnsListenerConfiguration(listenerEndpoint);
    }

    protected override AmazonSnsSubscriberConfiguration createSubscriberExpression(AmazonSnsTopic subscriberEndpoint)
    {
        return new AmazonSnsSubscriberConfiguration(subscriberEndpoint);
    }
    
    /// <summary>
    /// Override Wolverine's default queue policy that is set on SQS queues configured
    /// by the SNS transport from a subscription
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AmazonSnsTransportConfiguration QueuePolicyForSqsSubscriptions(
        Func<SqsTopicDescription, string> builder)
    {
        Transport.QueuePolicyBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        return this;
    }

    /// <summary>
    ///     Add credentials for the connection to AWS SQS
    /// </summary>
    /// <param name="credentials"></param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration Credentials(AWSCredentials credentials)
    {
        Transport.CredentialSource = _ => credentials;
        return this;
    }

    /// <summary>
    ///     Add a credential source for the connection to AWS SQS
    /// </summary>
    /// <param name="credentialSource"></param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration Credentials(Func<IWolverineRuntime, AWSCredentials> credentialSource)
    {
        Transport.CredentialSource = credentialSource;
        return this;
    }

    /// <summary>
    ///     Direct this application to use a LocalStack connection when
    ///     the system is detected to be running with EnvironmentName == "Development"
    /// </summary>
    /// <param name="port">Port to connect to LocalStack. Default is 4566</param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration UseLocalStackIfDevelopment(int port = 4566)
    {
        Transport.LocalStackPort = port;
        Transport.UseLocalStackInDevelopment = true;
        return this;
    }

    /// <summary>
    /// Override the sending behavior for unknown or missing tenant ids when using broker-per-tenant Amazon SNS
    /// multi-tenancy (GH-3305). See <see cref="TenantedIdBehavior"/>. Default is
    /// <see cref="Wolverine.Transports.Sending.TenantedIdBehavior.FallbackToDefault"/> unless changed.
    /// </summary>
    /// <param name="behavior"></param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration TenantIdBehavior(TenantedIdBehavior behavior)
    {
        Transport.TenantedIdBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Register a tenant that is served by its own dedicated Amazon SNS connection (typically a distinct region or
    /// <c>ServiceURL</c>) while sharing the topic topology declared on this transport. The tenant inherits the
    /// parent's AWS credentials and provisioning behavior; use <paramref name="configure"/> to point the tenant at
    /// its own region or endpoint. Outbound messages carrying a matching <see cref="Envelope.TenantId"/> are
    /// published to this tenant's connection.
    ///
    /// SNS is publish-only, so this adds a tenant-specific <em>publisher</em>. Inbound tenant traffic is consumed by
    /// the paired per-tenant Amazon SQS subscriptions — see the Amazon SQS broker-per-tenant support.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="configure">Configuration applied to the tenant's own <see cref="AmazonSimpleNotificationServiceConfig"/>.</param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration AddTenant(string tenantId,
        Action<AmazonSimpleNotificationServiceConfig> configure)
    {
        if (tenantId.IsEmpty()) throw new ArgumentOutOfRangeException(nameof(tenantId), "Empty or null tenantId");
        ArgumentNullException.ThrowIfNull(configure);

        // Deferred: applied in AmazonSnsTenant.Compile() after the parent connection is seeded onto the tenant, so
        // the tenant only overrides the axes it sets and inherits the rest.
        Transport.Tenants[tenantId].Configure = configure;

        return this;
    }

    /// <summary>
    /// Register a tenant that is served by its own dedicated Amazon SNS account, identified by its own
    /// <paramref name="credentials"/>, while sharing the topic topology declared on this transport. Use the optional
    /// <paramref name="configure"/> to also point the tenant at its own region or <c>ServiceURL</c>; if omitted the
    /// tenant inherits the parent's region/endpoint. Outbound messages carrying a matching
    /// <see cref="Envelope.TenantId"/> are published to this tenant's account.
    ///
    /// SNS is publish-only, so this adds a tenant-specific <em>publisher</em>. Inbound tenant traffic is consumed by
    /// the paired per-tenant Amazon SQS subscriptions — see the Amazon SQS broker-per-tenant support.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="credentials">The AWS credentials for the tenant's dedicated account.</param>
    /// <param name="configure">Optional configuration applied to the tenant's own <see cref="AmazonSimpleNotificationServiceConfig"/>.</param>
    /// <returns></returns>
    public AmazonSnsTransportConfiguration AddTenant(string tenantId, AWSCredentials credentials,
        Action<AmazonSimpleNotificationServiceConfig>? configure = null)
    {
        if (tenantId.IsEmpty()) throw new ArgumentOutOfRangeException(nameof(tenantId), "Empty or null tenantId");
        ArgumentNullException.ThrowIfNull(credentials);

        var tenant = Transport.Tenants[tenantId];
        tenant.Transport.CredentialSource = _ => credentials;
        // Deferred: applied in AmazonSnsTenant.Compile() after the parent connection is seeded onto the tenant.
        tenant.Configure = configure;

        return this;
    }
}
