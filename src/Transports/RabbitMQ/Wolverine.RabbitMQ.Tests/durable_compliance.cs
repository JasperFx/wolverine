using System.Threading.Tasks;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using TestingSupport.Compliance;
using Wolverine.Marten;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class RabbitMqTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public RabbitMqTransportFixture() : base($"rabbitmq://queue/{RabbitTesting.NextQueueName()}".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        var queueName = RabbitTesting.NextQueueName();
        OutboundAddress = $"rabbitmq://queue/{queueName}".ToUri();

        await SenderIs(opts =>
        {
            var listener = $"listener{RabbitTesting.Number}";

            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine("rabbit_sender");


            opts.UseRabbitMq()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .DeclareQueue(queueName)
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureSenders(x => x.UseDurableOutbox()).EnableWolverineControlQueues();

            opts.ListenToRabbitQueue(listener).TelemetryEnabled(false);
        });

        await ReceiverIs(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DisableNpgsqlLogging = true;
                })
                .IntegrateWithWolverine("rabbit_receiver");


            opts.UseRabbitMq()
                .ConfigureListeners(x => x.UseDurableInbox())
                .ConfigureSenders(x => x.UseDurableOutbox()).EnableWolverineControlQueues();;
            opts.ListenToRabbitQueue(queueName).TelemetryEnabled(false);
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("acceptance")]
public class durable_compliance : TransportCompliance<RabbitMqTransportFixture>;