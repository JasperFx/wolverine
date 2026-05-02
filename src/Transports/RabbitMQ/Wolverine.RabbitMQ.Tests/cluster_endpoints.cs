using JasperFx;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class cluster_endpoints
{
    [Fact]
    public void description_has_no_cluster_nodes_when_list_is_empty()
    {
        var factory = new ConnectionFactory { HostName = "localhost" };
        var description = new RabbitMqConnectionDescription(factory, Array.Empty<AmqpTcpEndpoint>());

        var rendered = description.ToDescription();

        rendered.Properties.ShouldNotContain(p => p.Name.StartsWith("ClusterNodes"));
    }

    [Fact]
    public void description_renders_cluster_nodes_as_indexed_entries()
    {
        var factory = new ConnectionFactory { HostName = "primary" };
        var nodes = new[]
        {
            new AmqpTcpEndpoint("rabbit-1", 5672),
            new AmqpTcpEndpoint("rabbit-2", 5673)
        };

        var rendered = new RabbitMqConnectionDescription(factory, nodes).ToDescription();

        rendered.Properties.Where(p => p.Name == "ClusterNodes[0]").ShouldHaveSingleItem()
            .Value.ShouldBe("rabbit-1:5672");
        rendered.Properties.Where(p => p.Name == "ClusterNodes[1]").ShouldHaveSingleItem()
            .Value.ShouldBe("rabbit-2:5673");
    }

    [Fact]
    public void add_cluster_node_throws_when_transport_was_not_initialised()
    {
        var options = new WolverineOptions();
        var transport = new RabbitMqTransport();
        // Deliberately not calling ConfigureFactory so ConnectionFactory stays null.
        var expression = new RabbitMqTransportExpression(transport, options);

        Should.Throw<InvalidOperationException>(() => expression.AddClusterNode("rabbit-1"));
    }

    [Fact]
    public void add_cluster_node_appends_in_order()
    {
        var options = new WolverineOptions();
        var expression = options.UseRabbitMq(_ => { });

        expression
            .AddClusterNode("rabbit-1", 5672)
            .AddClusterNode("rabbit-2", 5672)
            .AddClusterNode("rabbit-3", 5672);

        var endpoints = options.RabbitMqTransport().AmqpTcpEndpoints;
        endpoints.Count.ShouldBe(3);
        endpoints[0].HostName.ShouldBe("rabbit-1");
        endpoints[1].HostName.ShouldBe("rabbit-2");
        endpoints[2].HostName.ShouldBe("rabbit-3");
    }

    [Fact]
    public void add_cluster_node_copies_ssl_settings_as_fresh_instance()
    {
        var options = new WolverineOptions();
        var expression = options.UseRabbitMq(f =>
        {
            f.Ssl.Enabled = true;
            f.Ssl.ServerName = "rabbit-cluster";
        });

        expression.AddClusterNode("rabbit-1", 5671);

        var transport = options.RabbitMqTransport();
        var endpoint = transport.AmqpTcpEndpoints.ShouldHaveSingleItem();

        endpoint.Ssl.Enabled.ShouldBeTrue();
        endpoint.Ssl.ServerName.ShouldBe("rabbit-cluster");
        // Distinct instance — mutating the factory afterwards must not leak in.
        endpoint.Ssl.ShouldNotBeSameAs(transport.ConnectionFactory!.Ssl);
    }

    [Fact]
    public void add_cluster_node_with_endpoint_stores_supplied_object_by_reference()
    {
        var options = new WolverineOptions();
        var expression = options.UseRabbitMq(_ => { });
        var supplied = new AmqpTcpEndpoint("custom-host", 1234, new SslOption { Enabled = true, ServerName = "custom-tls" });

        expression.AddClusterNode(supplied);

        var stored = options.RabbitMqTransport().AmqpTcpEndpoints.ShouldHaveSingleItem();
        stored.ShouldBeSameAs(supplied);
    }

    [Fact]
    public void add_cluster_node_with_default_port_resolves_to_amqp_default()
    {
        var options = new WolverineOptions();
        var expression = options.UseRabbitMq(_ => { });

        expression.AddClusterNode("rabbit-1");

        var endpoint = options.RabbitMqTransport().AmqpTcpEndpoints.ShouldHaveSingleItem();
        endpoint.Port.ShouldBe(5672);
    }

    [Fact]
    public void add_cluster_node_endpoint_overload_throws_when_transport_was_not_initialised()
    {
        var options = new WolverineOptions();
        var transport = new RabbitMqTransport();
        var expression = new RabbitMqTransportExpression(transport, options);
        var endpoint = new AmqpTcpEndpoint("rabbit-1", 5672);

        Should.Throw<InvalidOperationException>(() => expression.AddClusterNode(endpoint));
    }

    [Fact]
    public void virtual_host_tenant_inherits_parent_cluster_nodes()
    {
        var options = new WolverineOptions();
        var parent = options
            .UseRabbitMq(f => { f.HostName = "primary"; f.UserName = "guest"; })
            .AddClusterNode("rabbit-1", 5672)
            .AddClusterNode("rabbit-2", 5672);

        parent.AddTenant("acme", "vh-acme");

        var parentTransport = options.RabbitMqTransport();
        var tenant = parentTransport.Tenants["acme"];
        tenant.Compile(parentTransport);

        tenant.Transport.AmqpTcpEndpoints.Count.ShouldBe(2);
        tenant.Transport.AmqpTcpEndpoints[0].HostName.ShouldBe("rabbit-1");
        tenant.Transport.AmqpTcpEndpoints[1].HostName.ShouldBe("rabbit-2");
    }

    // Regression guard for the documented limitation:
    // tenants configured via AddTenant(tenantId, Uri) bring their own transport
    // and must not inherit cluster endpoints from the parent. If a future change
    // accidentally moves the endpoint-copy loop outside the VirtualHostName branch,
    // this test will start failing.
    [Fact]
    public void uri_tenant_does_not_inherit_parent_cluster_nodes()
    {
        var options = new WolverineOptions();
        var parent = options
            .UseRabbitMq(f => { f.HostName = "primary"; })
            .AddClusterNode("rabbit-1", 5672);

        parent.AddTenant("acme", new Uri("amqp://other-host:5672/vh-acme"));

        var parentTransport = options.RabbitMqTransport();
        var tenant = parentTransport.Tenants["acme"];
        tenant.Compile(parentTransport);

        tenant.Transport.AmqpTcpEndpoints.ShouldBeEmpty();
    }

    [Fact]
    public void compiling_virtual_host_tenant_twice_does_not_duplicate_cluster_nodes()
    {
        var options = new WolverineOptions();
        var parent = options
            .UseRabbitMq(f => { f.HostName = "primary"; })
            .AddClusterNode("rabbit-1", 5672)
            .AddClusterNode("rabbit-2", 5672);

        parent.AddTenant("acme", "vh-acme");

        var parentTransport = options.RabbitMqTransport();
        var tenant = parentTransport.Tenants["acme"];
        tenant.Compile(parentTransport);
        tenant.Compile(parentTransport);

        tenant.Transport.AmqpTcpEndpoints.Count.ShouldBe(2);
    }

    [Fact, Trait("Category", "Flaky")]
    public async Task can_publish_and_receive_through_cluster_code_path()
    {
        var queueName = RabbitTesting.NextQueueName();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq(f => { f.UserName = "guest"; f.Password = "guest"; })
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .AddClusterNode("localhost", 5672);

                opts.PublishMessage<ClusterPing>().ToRabbitQueue(queueName);
                opts.ListenToRabbitQueue(queueName);

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new ClusterPing("hello"));

        session.Received.SingleMessage<ClusterPing>().Text.ShouldBe("hello");
    }
}

public record ClusterPing(string Text);

public class ClusterPingHandler
{
    public void Handle(ClusterPing _) { }
}
