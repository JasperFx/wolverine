using IntegrationTests;
using JasperFx.Core;
using Marten;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

[Collection("marten")]
public class DurableTcpTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public DurableTcpTransportFixture() : base($"tcp://localhost:{PortFinder.GetAvailablePort()}/incoming".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        OutboundAddress = $"tcp://localhost:{PortFinder.GetAvailablePort()}/incoming/durable".ToUri();

        await SenderIs(opts =>
        {
            var receivingUri = $"tcp://localhost:{PortFinder.GetAvailablePort()}/incoming/durable".ToUri();
            opts.ListenForMessagesFrom(receivingUri).TelemetryEnabled(false);

            opts.Services.AddMarten(o =>
            {
                o.Connection(Servers.PostgresConnectionString);
                o.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();

            opts.Durability.Mode = DurabilityMode.Solo;
        });

        await ReceiverIs(opts =>
        {
            opts.ListenForMessagesFrom(OutboundAddress);

            opts.Services.AddMarten(o =>
            {
                o.Connection(Servers.PostgresConnectionString);
                o.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine();

            opts.Durability.Mode = DurabilityMode.Solo;
        });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("marten")]
public class DurableTcpTransportCompliance : TransportCompliance<DurableTcpTransportFixture>;