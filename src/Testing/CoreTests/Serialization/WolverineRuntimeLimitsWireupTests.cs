using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

[Collection("EnvelopeSerializerLimits")]
public class WolverineRuntimeLimitsWireupTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        EnvelopeSerializer.Limits = EnvelopeReaderLimits.Default;
        return Task.CompletedTask;
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
            .StartAsync();

        EnvelopeSerializer.Limits.MaxBatchSize.ShouldBe(4242);
        EnvelopeSerializer.Limits.MaxDataSize.ShouldBe(9 * 1024 * 1024);
        EnvelopeSerializer.Limits.MaxHeaderCount.ShouldBe(256);
    }

    [Fact]
    public async Task host_startup_with_default_options_leaves_default_limits()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(_ => { })
            .StartAsync();

        EnvelopeSerializer.Limits.ShouldBe(EnvelopeReaderLimits.Default);
    }
}
