using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace SlowTests.TcpTransport;

public class LightweightTcpFixture : TransportComplianceFixture, IAsyncLifetime
{
    public LightweightTcpFixture() : base($"tcp://localhost:{PortFinder.GetAvailablePort()}/incoming".ToUri())
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts => { opts.ListenAtPort(PortFinder.GetAvailablePort()); });

        await ReceiverIs(opts => { opts.ListenAtPort(OutboundAddress.Port); });
    }

    public async Task DisposeAsync()
    {
        await DisposeAsync();
    }
}

[Collection("compliance")]
public class LightweightTcpTransportCompliance : TransportCompliance<LightweightTcpFixture>
{
}