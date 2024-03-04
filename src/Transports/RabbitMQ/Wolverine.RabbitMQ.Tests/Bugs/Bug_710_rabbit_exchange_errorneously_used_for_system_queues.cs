using System.Diagnostics;
using System.Runtime.CompilerServices;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport.Compliance;
using Wolverine.Marten;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_710_rabbit_exchange_errorneously_used_for_system_queues : RabbitMQContext
{
    [Fact]
    public async Task start_system_with_declared_exchange()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .DeclareExchange("NotControlExchange", ex =>
                    {
                        ex.BindQueue("NotControlQueue");
                    })
                    .AutoProvision();

                opts.PublishMessage<Message1>().ToRabbitExchange("NotControlExchange");

                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();
            }).StartAsync();

        var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
        
        options.Transports.NodeControlEndpoint.Uri.Scheme.ShouldBe("dbcontrol");
    }
}