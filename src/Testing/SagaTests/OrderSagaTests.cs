using System.Reflection;
using Alba;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderSagaSample;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Marten;

namespace SagaTests;

public class When_starting_an_order : IAsyncLifetime
{
    private Order? _order;
    private IAlbaHost? _host;

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
                                    opts.Connection((string?)Servers.PostgresConnectionString);
                                    opts.DatabaseSchemaName = "orders";
                                }
                            )
                            .IntegrateWithWolverine();
                        ;
                    }
                )
                .UseWolverine(
                    options =>
                    {
                        // TODO -- this should not be necessary
                        options.Discovery.IncludeAssembly(Assembly.GetAssembly(typeof(Order)));
                    }
                )
                .StartAlbaAsync();

        var orderId = Guid.NewGuid()
            .ToString();

        await _host.InvokeMessageAndWaitAsync(
            new StartOrder(orderId)
        );

        var session = _host.Services.GetService<IQuerySession>();
        _order = session?.Load<Order>(orderId);
    }

    [Fact]
    public void should_exist() => _order.ShouldNotBeNull();

    [Fact]
    public void should_not_be_completed() => _order?.IsCompleted()
        .ShouldBeFalse();


    public async Task DisposeAsync() => await _host.DisposeAsync();
}
