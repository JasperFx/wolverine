using Microsoft.Extensions.Hosting;
using Wolverine;

namespace DocumentationSamples;

public class MetricsSamples
{
    public static async Task configure_metrics_header()
    {
        #region sample_using_organization_tagging_middleware

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Add this middleware to all handlers where the message can be cast to
                // IOrganizationRelated
                opts.Policies.ForMessagesOfType<IOrganizationRelated>().AddMiddleware(typeof(OrganizationTaggingMiddleware));
            }).StartAsync();

        #endregion
    }
    
    
}

public record SomeMessage(string Name);

public record SomeResponse(string Name);


public static class SomeOperationHandler
{
    #region sample_tenant_id_tagging

    public static async Task publish_operation(IMessageBus bus, string tenantId, string name)
    {
        // All outgoing messages or executed messages from this 
        // IMessageBus object will be tagged with the tenant id
        bus.TenantId = tenantId;
        await bus.PublishAsync(new SomeMessage(name));
    }

    #endregion
}

#region sample_organization_tagging_middleware

// Common interface on message types within our system
public interface IOrganizationRelated
{
    string OrganizationCode { get; }
}

// Middleware just to add a metrics tag for the organization code
public static class OrganizationTaggingMiddleware
{
    public static void Before(IOrganizationRelated command, Envelope envelope)
    {
        envelope.SetMetricsTag("org.code", command.OrganizationCode);
    }
}

#endregion

