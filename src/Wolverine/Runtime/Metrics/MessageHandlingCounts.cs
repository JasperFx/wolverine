using JasperFx;
using JasperFx.Core;

namespace Wolverine.Runtime.Metrics;


public class MessageHandlingCounts
{
    public string MessageType { get; }
    public Uri Destination { get; }

    public MessageHandlingCounts(string messageType, Uri destination)
    {
        MessageType = messageType;
        Destination = destination;
    }

    public LightweightCache<string, PerTenantTracking> PerTenant { get; } =
        new(tenantId => new PerTenantTracking(tenantId));

    public void Increment(IHandlerMetricsData metricsData)
    {
        var perTenant = PerTenant[metricsData.TenantId ?? StorageConstants.DefaultTenantId];
        metricsData.Apply(perTenant);
    }

    public void Clear()
    {
        foreach (var tracking in PerTenant)
        {
            tracking.Clear();
        }
        
    }
}