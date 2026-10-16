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
/// GH-5051. The durability mode wins over an endpoint's own configuration. Endpoint.Compile() used to apply the
/// Serverless policies before the endpoint's delayed configuration, so a per-endpoint UseDurableOutbox() stayed
/// Durable under plain Serverless and a per-endpoint SendInline() skipped the outbox under ServerlessWithOutbox.
/// With resource setup on startup the endpoints are compiled before the runtime's StartAsync, too
/// </summary>
public class serverless_durability_mode_wins_5051
{
    private const string SchemaName = "gh5051_rmq";

    private static Task<IHost> startAsync(DurabilityMode mode, bool resourceSetupOnStartup,
        Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.UseRabbitMq().AutoProvision();
                configure(opts);

                if (resourceSetupOnStartup) opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task plain_serverless_downgrades_a_durable_rabbit_endpoint_to_inline(bool resourceSetupOnStartup)
    {
        var queueName = $"gh5051-serverless-{Guid.NewGuid():N}";
        using var host = await startAsync(DurabilityMode.Serverless, resourceSetupOnStartup,
            opts => opts.PublishMessage<ModeWinsEvent>().ToRabbitQueue(queueName).UseDurableOutbox());

        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri($"rabbitmq://queue/{queueName}"));

        agent.Endpoint.Mode.ShouldBe(EndpointMode.Inline);
        agent.IsDurable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task serverless_with_outbox_keeps_an_inline_rabbit_endpoint_durable(bool resourceSetupOnStartup)
    {
        var queueName = $"gh5051-outbox-{Guid.NewGuid():N}";
        using var host = await startAsync(DurabilityMode.ServerlessWithOutbox, resourceSetupOnStartup,
            opts => opts.PublishMessage<ModeWinsEvent>().ToRabbitQueue(queueName).SendInline());

        var agent = host.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri($"rabbitmq://queue/{queueName}"));

        agent.Endpoint.Mode.ShouldBe(EndpointMode.Durable);
        agent.IsDurable.ShouldBeTrue();
    }
}

public record ModeWinsEvent(Guid Id);
