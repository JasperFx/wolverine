using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_2304_conventional_routing_ignores_durable_outbox_policy : IAsyncLifetime, IDisposable
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine();

            opts.UseRabbitMq()
                .UseConventionalRouting()
                .AutoProvision()
                .AutoPurgeOnStartup();

            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

            opts.DisableConventionalDiscovery();
        });
    }

    [Fact]
    public void conventionally_routed_endpoint_should_be_durable()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var routes = runtime.RoutingFor(typeof(Bug2304Message))
            .ShouldBeOfType<MessageRouter<Bug2304Message>>()
            .Routes;

        routes.Length.ShouldBeGreaterThan(0);

        var route = routes.Single().ShouldBeOfType<MessageRoute>();
        var endpoint = route.Sender.Endpoint;

        // The endpoint should be Durable because of UseDurableOutboxOnAllSendingEndpoints()
        endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _host?.Dispose();
    }
}

public class Bug2304Message;

public class Bug2304Response;

public static class Bug2304Handler
{
    public static void Handle(Bug2304Message message)
    {
        // no-op
    }
}
