using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Serialization;

[Collection("EnvelopeSerializerLimits")]
public class WolverineRuntimeLimitsWireupTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>await  ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        EnvelopeSerializer.Limits = EnvelopeReaderLimits.Default;
        WireProtocol.MaxFrameSize = WireProtocol.DefaultMaxFrameSize;
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task host_startup_publishes_configured_limits_to_envelope_serializer()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MaxIncomingEnvelopeBatchSize = 4242;
                opts.MaxIncomingEnvelopeDataSize = 9 * 1024 * 1024;
                opts.MaxIncomingEnvelopeHeaderCount = 256;
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        EnvelopeSerializer.Limits.MaxBatchSize.ShouldBe(4242);
        EnvelopeSerializer.Limits.MaxDataSize.ShouldBe(9 * 1024 * 1024);
        EnvelopeSerializer.Limits.MaxHeaderCount.ShouldBe(256);
    }

    [Fact]
    public async Task host_startup_with_default_options_leaves_default_limits()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(_ => { })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        EnvelopeSerializer.Limits.ShouldBe(EnvelopeReaderLimits.Default);
        WireProtocol.MaxFrameSize.ShouldBe(WireProtocol.DefaultMaxFrameSize);
    }

    [Fact]
    public async Task host_startup_publishes_configured_tcp_frame_size_to_wire_protocol()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MaxIncomingTcpFrameSize = 7 * 1024 * 1024;
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        WireProtocol.MaxFrameSize.ShouldBe(7 * 1024 * 1024);
    }
}
