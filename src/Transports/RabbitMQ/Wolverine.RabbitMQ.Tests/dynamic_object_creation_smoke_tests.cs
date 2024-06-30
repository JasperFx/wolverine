using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class dynamic_object_creation_smoke_tests : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public void create_new_exchange_queue_and_binding_then_unbind()
    {
        var exchangeName = "dynamic_" + RabbitTesting.NextExchangeName();
        var queueName = "dynamic_" + RabbitTesting.NextQueueName();
        var bindingKey = Guid.NewGuid().ToString();

        #region sample_dynamic_creation_of_rabbit_mq_objects

        // _host is an IHost
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        // Declare new Exchanges, Queues, and Bindings at runtime
        runtime.ModifyRabbitMqObjects(o =>
        {
            var queue = o.DeclareQueue(queueName);
            var exchange = o.DeclareExchange(exchangeName);
            queue.BindExchange(exchange.ExchangeName, bindingKey);
        });

        // Unbind a queue from an exchange
        runtime.UnBindRabbitMqQueue(queueName, exchangeName, bindingKey);

        #endregion
    }
}