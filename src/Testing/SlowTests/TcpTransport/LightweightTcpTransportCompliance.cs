using JasperFx.Core;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace SlowTests.TcpTransport;

public class LightweightTcpFixture : TransportComplianceFixture, IAsyncLifetime
{
    public LightweightTcpFixture() : base($"tcp://localhost:{PortFinder.GetAvailablePort()}/incoming".ToUri())
    {
    }

    public async ValueTask InitializeAsync()
    {
        await SenderIs(opts => { opts.ListenAtPort(PortFinder.GetAvailablePort()); });

        await ReceiverIs(opts => { opts.ListenAtPort(OutboundAddress.Port); });
    }

}

[Collection("compliance")]
public class LightweightTcpTransportCompliance : TransportCompliance<LightweightTcpFixture>;