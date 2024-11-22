using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Internals;

public class multi_tenancy_internals
{
    [Fact]
    public void create_tenanted_transport_from_virtual_host()
    {
        var parent = new RabbitMqTransport();
        parent.ConfigureFactory(f =>
        {
            f.Port = 5673;
            f.VirtualHost = "/";
        });

        var tenant = new RabbitMqTenant("one", "one");
        var tenantTransport = tenant.BuildTransport(parent);
        
        tenantTransport.ConnectionFactory.VirtualHost.ShouldBe("one");
        tenantTransport.ConnectionFactory.Port.ShouldBe(5673);
    }

    [Fact]
    public void create_tenanted_transport_from_precanned_connection_factory()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f => f.VirtualHost = "one");
        var tenant = new RabbitMqTenant("one", transport);
        
        tenant.BuildTransport(new RabbitMqTransport()).ShouldBeSameAs(transport);
    }
}