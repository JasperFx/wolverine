using IntegrationTests;
using JasperFx.Core;
using Marten;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Util;

namespace MartenTests;

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

public class DurableTcpTransportCompliance : TransportCompliance<DurableTcpTransportFixture>;