using System.Threading.Tasks;
using IntegrationTests;
using Marten;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Marten;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Persistence.Testing.Marten;

[Collection("marten")]
public class DurableTcpTransportFixture : SendingComplianceFixture, IAsyncLifetime
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
            opts.ListenForMessagesFrom(receivingUri);

            opts.Services.AddMarten(o =>
            {
                o.Connection(Servers.PostgresConnectionString);
                o.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();
        });

        await ReceiverIs(opts =>
        {
            opts.ListenForMessagesFrom(OutboundAddress);

            opts.Services.AddMarten(o =>
            {
                o.Connection(Servers.PostgresConnectionString);
                o.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine();
        });
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }
}

[Collection("marten")]
public class DurableTcpTransportCompliance : SendingCompliance<DurableTcpTransportFixture>
{
}
