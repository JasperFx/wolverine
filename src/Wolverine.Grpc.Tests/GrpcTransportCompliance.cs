using Wolverine.ComplianceTests.Compliance;
using Wolverine.Grpc;
using Xunit;

public class GrpcComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public const int ReceiverPort = 5150;
    public const int SenderPort = 5151;

    public GrpcComplianceFixture() : base(new Uri($"grpc://localhost:{ReceiverPort}"), 30)
    {
    }

    public async Task InitializeAsync()
    {
        OutboundAddress = new Uri($"grpc://localhost:{ReceiverPort}");

        await ReceiverIs(opts =>
        {
            opts.ListenAtGrpcPort(ReceiverPort);
        });

        await SenderIs(opts =>
        {
            opts.ListenAtGrpcPort(SenderPort).UseForReplies();
            opts.PublishAllMessages().ToGrpcEndpoint("localhost", ReceiverPort);
        });
    }

    public new Task DisposeAsync() => Task.CompletedTask;
}

[Collection("GrpcSerialTests")]
public class GrpcTransportCompliance : TransportCompliance<GrpcComplianceFixture>;
