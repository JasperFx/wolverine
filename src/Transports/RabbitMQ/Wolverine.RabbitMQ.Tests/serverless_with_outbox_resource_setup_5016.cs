using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5016. Resource setup on startup (AddResourceSetupOnStartup, ResetResourceState, `resources setup`)
/// compiles the broker endpoints before the runtime's StartAsync, and Endpoint.Compile() applies endpoint
/// policies only once. The ServerlessWithOutbox policy used to be added in StartAsync, so with resource setup
/// every RabbitMQ sending endpoint stayed Inline and the outbox was silently off.
/// </summary>
public class serverless_with_outbox_resource_setup_5016
{
    private const string SchemaName = "serverless_rmq_setup";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_sending_endpoint_is_durable(bool resourceSetupOnStartup)
    {
        var queueName = $"serverless-setup-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.UseRabbitMq().AutoProvision();
                opts.PublishMessage<ServerlessSetupEvent>().ToRabbitQueue(queueName);

                if (resourceSetupOnStartup) opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri($"rabbitmq://queue/{queueName}"));

        agent.Endpoint.Mode.ShouldBe(EndpointMode.Durable);
        agent.IsDurable.ShouldBeTrue();
    }
}

public record ServerlessSetupEvent(Guid Id);
