using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Transports.Sending;
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
        var tenantTransport = tenant.Compile(parent);
        
        tenantTransport.ConnectionFactory!.VirtualHost.ShouldBe("one");
        tenantTransport.ConnectionFactory!.Port.ShouldBe(5673);
    }

    [Fact]
    public void create_tenanted_transport_from_precanned_connection_factory()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(f => f.VirtualHost = "one");
        var tenant = new RabbitMqTenant("one", transport);
        
        tenant.Compile(new RabbitMqTransport()).ShouldBeSameAs(transport);
    }

    [Fact]
    public void default_tenant_id_behavior()
    {
        var transport = new RabbitMqTransport();
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.FallbackToDefault);
    }

    [Fact]
    public void add_tenant_by_virtual_host()
    {
        var transport = new RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());
        expression.AddTenant("one", "vh1");
        
        transport.Tenants["one"].VirtualHostName.ShouldBe("vh1");
    }

    [Fact]
    public void add_tenant_by_uri()
    {
        var transport = new RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());
        expression.AddTenant("one", new Uri("amqp://server1"));
        
        transport.Tenants["one"].Transport!.ConnectionFactory!.HostName.ShouldBe("server1");
    }

    [Fact]
    public void add_tenant_by_explicit_configuration()
    {
        var transport = new RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());
        expression.AddTenant("one", c => c.HostName = "server2");

        transport.Tenants["one"].Transport!.ConnectionFactory!.HostName.ShouldBe("server2");
    }

    [Fact]
    public void global_dead_letter_queueing_applies_to_new_tenants()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(_ => { });
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());

        expression.CustomizeDeadLetterQueueing(new DeadLetterQueue("errors"));
        expression.AddTenant("one", "vh1");

        var tenantTransport = transport.Tenants["one"].Compile(transport);
        tenantTransport.DeadLetterQueue.QueueName.ShouldBe("errors");
    }

    [Fact]
    public void global_dead_letter_queueing_applies_to_existing_tenants()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(_ => { });
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());

        expression.AddTenant("one", "vh1");
        expression.CustomizeDeadLetterQueueing(new DeadLetterQueue("errors"));

        var tenantTransport = transport.Tenants["one"].Compile(transport);
        tenantTransport.DeadLetterQueue.QueueName.ShouldBe("errors");
    }

    [Fact]
    public void channel_creation_options_apply_to_virtual_host_tenants()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(_ => { });
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());

        expression.ConfigureChannelCreation(o =>
        {
            o.PublisherConfirmationsEnabled = true;
            o.PublisherConfirmationTrackingEnabled = true;
        });
        expression.AddTenant("one", "vh1");

        var tenantTransport = transport.Tenants["one"].Compile(transport);

        var options = resolveChannelOptions(tenantTransport);

        options.PublisherConfirmationsEnabled.ShouldBeTrue();
        options.PublisherConfirmationTrackingEnabled.ShouldBeTrue();
    }

    [Fact]
    public void channel_creation_options_apply_to_tenants_added_before_the_configuration()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(_ => { });
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());

        expression.AddTenant("one", "vh1");
        expression.ConfigureChannelCreation(o => o.ConsumerDispatchConcurrency = 5);

        var tenantTransport = transport.Tenants["one"].Compile(transport);

        var options = resolveChannelOptions(tenantTransport);

        options.ConsumerDispatchConcurrency.ShouldBe((ushort?)5);
    }

    [Fact]
    public void channel_creation_options_apply_to_tenants_with_their_own_transport()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(_ => { });
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());

        expression.ConfigureChannelCreation(o => o.PublisherConfirmationsEnabled = true);
        expression.AddTenant("one", new Uri("amqp://server1"));

        var tenantTransport = transport.Tenants["one"].Compile(transport);

        var options = resolveChannelOptions(tenantTransport);

        options.PublisherConfirmationsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void channel_creation_options_on_a_tenant_transport_win_over_the_parent()
    {
        var transport = new RabbitMqTransport();
        transport.ConfigureFactory(_ => { });
        var expression = new RabbitMqTransportExpression(transport, new WolverineOptions());
        expression.ConfigureChannelCreation(o => o.ConsumerDispatchConcurrency = 5);

        var tenantTransport = new RabbitMqTransport();
        tenantTransport.ConfigureFactory(_ => { });
        tenantTransport.ChannelCreationOptions = o => o.ConsumerDispatchConcurrency = 11;

        var tenant = new RabbitMqTenant("one", tenantTransport);
        tenant.Compile(transport);

        var options = resolveChannelOptions(tenantTransport);

        options.ConsumerDispatchConcurrency.ShouldBe((ushort?)11);
    }

    private static WolverineRabbitMqChannelOptions resolveChannelOptions(RabbitMqTransport transport)
    {
        var configure = transport.ChannelCreationOptions;
        configure.ShouldNotBeNull();

        var options = new WolverineRabbitMqChannelOptions();
        configure(options);

        return options;
    }
}
