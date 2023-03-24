using System.Reflection;
using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OrderSagaSample;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace SagaTests;

public class When_starting_an_order : IAsyncLifetime
{
    private Order? _order;
    private IAlbaHost? _host;

    public async Task InitializeAsync()
    {
        var connectionString = new NpgsqlConnectionStringBuilder()
        {
            Pooling = false,
            Port = 5433,
            Host = "localhost",
            CommandTimeout = 20,
            Database = "postgres",
            Password = "postgres",
            Username = "postgres"
        }.ToString();

        _host = await
            Host.CreateDefaultBuilder()
                .ConfigureServices(
                    services =>
                    {
                        services.AddMarten(
                                opts =>
                                {
                                    opts.Connection(connectionString);
                                    opts.DatabaseSchemaName = "orders";
                                }
                            )
                            .IntegrateWithWolverine();
                        ;
                    }
                )
                .UseWolverine(
                    options => { options.Discovery.IncludeAssembly(Assembly.GetAssembly(typeof(Order))); }
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
