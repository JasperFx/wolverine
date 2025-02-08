using System.Reflection;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using OrderSagaSample;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class When_starting_an_order : PostgresqlContext, IAsyncLifetime
{
    private IHost? _host;
    private Order? _order;

    public async Task InitializeAsync()
    {
        _host = await
            Host.CreateDefaultBuilder()
                .ConfigureServices(
                    services =>
                    {
                        services.AddMarten(
                                opts =>
                                {
                                    opts.Connection(Servers.PostgresConnectionString);
                                    opts.DatabaseSchemaName = "orders";
                                }
                            )
                            .IntegrateWithWolverine();

                        services.AddResourceSetupOnStartup();
                    }
                )
                .UseWolverine(options =>
                {
                    options.Discovery.IncludeAssembly(Assembly.GetAssembly(typeof(Order)));
                })
                .StartAsync();

        var orderId = Guid.NewGuid().ToString();

        await _host.InvokeMessageAndWaitAsync(new StartOrder(orderId));

        using var session = _host.DocumentStore().QuerySession();
        _order = await session.LoadAsync<Order>(orderId);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void should_exist()
    {
        _order.ShouldNotBeNull();
    }

    [Fact]
    public void should_not_be_completed()
    {
        _order?.IsCompleted()
            .ShouldBeFalse();
    }
}