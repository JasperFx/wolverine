using JasperFx.Core;
using RabbitMQ.Client;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqTenant
{
    public RabbitMqTenant(string tenantId, string virtualHostName)
    {
        TenantId = tenantId;
        VirtualHostName = virtualHostName ?? throw new ArgumentNullException(nameof(virtualHostName));
        Transport = new RabbitMqTransport();
    }

    public RabbitMqTenant(string tenantId, RabbitMqTransport transport)
    {
        TenantId = tenantId;
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public string TenantId { get; }
    public RabbitMqTransport Transport { get; private set; }
    public string? VirtualHostName { get; set; }

    internal LightweightCache<string, RabbitMqTransport> Tenants { get; } = new();

    public RabbitMqTransport Compile(RabbitMqTransport parent)
    {
        if (VirtualHostName.IsNotEmpty())
        {
            var props = typeof(ConnectionFactory).GetProperties();
            
            Transport.ConfigureFactory(f =>
            {
                foreach (var prop in props)
                {
                    if (!prop.CanWrite) continue;
                
                    prop.SetValue(f, prop.GetValue(parent.ConnectionFactory));
                }

                f.VirtualHost = VirtualHostName;
            });
        }

        return Transport!;
    }
}