using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// Regression coverage for GH-3564: when the claim-check store is registered in the container
/// (the pattern used by every <c>...FromServices</c> overload) rather than assigned explicitly
/// during the UseClaimCheck callback, the pipeline must actually use that store instead of
/// silently falling back to the file system.
/// </summary>
public class from_services_store_resolution : IAsyncLifetime
{
    // A single shared store stands in for a real external backend that both nodes point at.
    private readonly RecordingInMemoryClaimCheckStore _store = new();
    private IHost _publisher = null!;
    private IHost _receiver = null!;

    public async Task InitializeAsync()
    {
        CapturedMessages.Reset();

        var port = PortFinder.GetAvailablePort();

        _publisher = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(from_services_store_resolution).Assembly;

            // Mimic a ...FromServices overload: register the store in DI, but never assign
            // configuration.Store. Pre-fix, UseClaimCheck ignored this registration and built the
            // serializer around the file-system fallback.
            opts.UseClaimCheck(c => c.Options.Services.AddSingleton<IClaimCheckStore>(_store));

            opts.PublishMessage<BlobByteArrayMessage>().ToPort(port);
        }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(from_services_store_resolution).Assembly;

            opts.UseClaimCheck(c => c.Options.Services.AddSingleton<IClaimCheckStore>(_store));
            opts.ListenAtPort(port);
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver.StopAsync();
        await _publisher.StopAsync();
        _receiver.Dispose();
        _publisher.Dispose();
    }

    [Fact]
    public void di_registered_store_survives_UseClaimCheck()
    {
        // RemoveAll<IClaimCheckStore>() previously wiped the FromServices registration.
        _publisher.Services.GetRequiredService<IClaimCheckStore>().ShouldBeSameAs(_store);
        _receiver.Services.GetRequiredService<IClaimCheckStore>().ShouldBeSameAs(_store);
    }

    [Fact]
    public async Task blob_payload_is_off_loaded_to_the_di_registered_store()
    {
        var bytes = new byte[2048];
        Random.Shared.NextBytes(bytes);

        var session = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .SendMessageAndWaitAsync(new BlobByteArrayMessage("doc.pdf", bytes));

        // The round trip must succeed through the DI-registered backend...
        var received = session.Received.SingleMessage<BlobByteArrayMessage>();
        received.ShouldNotBeNull();
        received.Name.ShouldBe("doc.pdf");
        received.Payload!.ShouldBe(bytes);

        // ...and, decisively, the recording store must have seen the traffic. Pre-fix these were
        // zero because the payload went to the file-system fallback instead.
        _store.StoreCount.ShouldBeGreaterThan(0);
        _store.LoadCount.ShouldBeGreaterThan(0);
    }
}
