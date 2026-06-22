using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport.NServiceBus;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport.NServiceBus;

// Exercises the NServiceBus PostgreSQL interop transport end to end against a real
// PostgreSQL without requiring an NServiceBus host: a publishing Wolverine app writes
// rows into the NServiceBus-shaped queue table and a listening Wolverine app pops and
// handles them. This validates the send INSERT, the destructive Seq-ordered pop, and the
// envelope <-> (Headers + Body) mapping. True NServiceBus wire fidelity is covered
// separately by the wolverine-interop suite.
public class nservicebus_postgresql_transport_smoke : IAsyncLifetime
{
    private readonly string _queue = "nsb_interop_" + Guid.NewGuid().ToString("N");
    private IHost _publisher = null!;
    private IHost _receiver = null!;

    public async Task InitializeAsync()
    {
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbpublisher");
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                opts.PublishMessage<NsbInteropPing>().ToNServiceBusPostgresqlQueue(_queue);

                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "nsbreceiver");
                opts.UseNServiceBusPostgresqlInterop(autoProvision: true);

                opts.ListenToNServiceBusPostgresqlQueue(_queue);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NsbInteropPingHandler>();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task round_trips_a_message_through_the_nservicebus_queue_table()
    {
        var id = Guid.NewGuid();

        var tracked = await _publisher.TrackActivity()
            .Timeout(30.Seconds())
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<NsbInteropPing>(_receiver)
            .SendMessageAndWaitAsync(new NsbInteropPing(id, "hello"));

        var received = tracked.Received.SingleEnvelope<NsbInteropPing>();
        received.Message.ShouldBeOfType<NsbInteropPing>().Id.ShouldBe(id);
        received.Message.ShouldBeOfType<NsbInteropPing>().Name.ShouldBe("hello");
    }

    public async Task DisposeAsync()
    {
        await _publisher.StopAsync();
        _publisher.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }
}

public record NsbInteropPing(Guid Id, string Name);

public class NsbInteropPingHandler
{
    public static void Handle(NsbInteropPing message)
    {
        // handled; assertion is via tracking
    }
}
