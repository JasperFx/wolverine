using JasperFx.Core;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTenant
{
    public AzureServiceBusTenant(string tenantId)
    {
        TenantId = tenantId;
    }

    public string TenantId { get; set; }

    public AzureServiceBusTransport Transport { get; } = new();

    public void Compile(AzureServiceBusTransport parent)
    {
        Transport.SasCredential = parent.SasCredential;
        Transport.TokenCredential = parent.TokenCredential;
        Transport.NamedKeyCredential = parent.NamedKeyCredential;

        if (Transport.FullyQualifiedNamespace.IsEmpty() && Transport.ConnectionString.IsEmpty())
        {
            throw new InvalidOperationException(
                $"The AzureServiceBus Transport connection for tenant {TenantId} does not have either a fully qualified namespace or connection string");
        }
    }
}