using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

// GH-5702. A saga that implements IRevisioned gets its Version from Saga.Version. It declares nothing of its own, and
// Marten's numeric revision for the saga document is that property
public class irevisioned_saga_uses_saga_version_5702 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RoomBooking>();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "irevisioned_sagas_5702";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task each_save_moves_saga_version_and_the_document_revision_together()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new BookRoom(id));
        await _host.InvokeMessageAndWaitAsync(new AddBreakfast(id));
        await _host.InvokeMessageAndWaitAsync(new AddLateCheckout(id));

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        var booking = await session.LoadAsync<RoomBooking>(id, TestContext.Current.CancellationToken);

        booking.ShouldNotBeNull();
        booking.Breakfast.ShouldBeTrue();
        booking.LateCheckout.ShouldBeTrue();

        // Started, then updated twice
        booking.Version.ShouldBe(3);

        var metadata = await session.MetadataForAsync(booking, TestContext.Current.CancellationToken);
        metadata!.CurrentRevision.ShouldBe(booking.Version);
    }
}

public record BookRoom(Guid Id);

public record AddBreakfast(Guid Id);

public record AddLateCheckout(Guid Id);

#region sample_irevisioned_saga_needs_no_version_of_its_own
// IRevisioned.Version is Saga.Version, so there is nothing to declare
public class RoomBooking : Wolverine.Saga, IRevisioned
{
    public Guid Id { get; set; }
    public bool Breakfast { get; set; }
    public bool LateCheckout { get; set; }

    public static RoomBooking Start(BookRoom command) => new() { Id = command.Id };

    public void Handle(AddBreakfast command) => Breakfast = true;

    public void Handle(AddLateCheckout command) => LateCheckout = true;
}
#endregion
