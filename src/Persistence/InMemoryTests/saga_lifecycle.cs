using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;

namespace InMemoryTests;

public class saga_lifecycle : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await TestHosts.StartAsync(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ShipmentSaga));
        });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<ShipmentSaga?> load(Guid id)
    {
        await using var session = theHost.Services.GetRequiredService<IDocumentSessionFactory>().QuerySession();
        return await session.LoadAsync<ShipmentSaga>(id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_saga_is_started_updated_and_completed()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new StartShipment(id));
        (await load(id))!.Packed.ShouldBeFalse();

        await theHost.InvokeMessageAndWaitAsync(new PackShipment(id));
        (await load(id))!.Packed.ShouldBeTrue();

        await theHost.InvokeMessageAndWaitAsync(new DeliverShipment(id));
        (await load(id)).ShouldBeNull();
    }
}

public record StartShipment(Guid Id);

public record PackShipment(Guid Id);

public record DeliverShipment(Guid Id);

public class ShipmentSaga : Saga
{
    public Guid Id { get; set; }
    public bool Packed { get; set; }

    public static ShipmentSaga Start(StartShipment command) => new() { Id = command.Id };

    public void Handle(PackShipment command) => Packed = true;

    public void Handle(DeliverShipment command) => MarkCompleted();
}
