using Bobcat;
using Bobcat.Engine;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine.Marten;
using Wolverine.Postgresql;

namespace Wolverine.Bobcat.Tests;

// GH-5553 / GH-5556: GivenSaga, ThenSaga and ThenNoSaga read and write a saga through the persistence Wolverine uses
// for it, so the same steps work whether the saga is a Marten document, a row in the lightweight saga tables, or kept
// in memory

public record OpenShipment(Guid Id, string Carrier);

public record DeliverShipment(Guid Id);

public record CancelShipment(Guid Id);

public class Shipment : Saga
{
    public Guid Id { get; set; }
    public string Carrier { get; set; } = "";
    public bool Delivered { get; set; }

    public static Shipment Start(OpenShipment command) => new() { Id = command.Id, Carrier = command.Carrier };

    public void Handle(DeliverShipment command) => Delivered = true;

    public void Handle(CancelShipment command) => MarkCompleted();
}

public abstract class ShipmentHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    protected abstract void Configure(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Shipment));
                Configure(opts);
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public class MartenShipmentHost : ShipmentHost
{
    protected override void Configure(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_saga_documents";
                m.DisableNpgsqlLogging = true;
            })
            .IntegrateWithWolverine();
        opts.Policies.AutoApplyTransactions();
    }
}

public class LightweightShipmentHost : ShipmentHost
{
    protected override void Configure(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bobcat_saga_tables");
        opts.Policies.AutoApplyTransactions();
    }
}

public class InMemoryShipmentHost : ShipmentHost
{
    protected override void Configure(WolverineOptions opts)
    {
    }
}

[CollectionDefinition(nameof(MartenShipmentCollection))]
public class MartenShipmentCollection : ICollectionFixture<MartenShipmentHost>;

[CollectionDefinition(nameof(LightweightShipmentCollection))]
public class LightweightShipmentCollection : ICollectionFixture<LightweightShipmentHost>;

[CollectionDefinition(nameof(InMemoryShipmentCollection))]
public class InMemoryShipmentCollection : ICollectionFixture<InMemoryShipmentHost>;

public abstract class saga_state(ShipmentHost app) : WolverineSpec(app.Host)
{
    [Fact]
    public async Task a_saga_arranged_by_GivenSaga_is_the_one_the_act_handles()
    {
        var theShipment = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenSaga<Shipment>(new Shipment { Id = theShipment, Carrier = "UPS" });
            await WhenReceived(new DeliverShipment(theShipment));
            await ThenSaga<Shipment>(theShipment, Specify<Shipment>()
                .With(x => x.Carrier, "UPS")
                .With(x => x.Delivered, true));
        });

        recording.Steps[0].Text.ShouldStartWith("a Shipment saga #theShipment");
        recording.Steps[0].Status.ShouldBe(ResultStatus.success);
        var then = recording.Steps.Last();
        then.Text.ShouldBe("the Shipment saga #theShipment matches");
        then.Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_saga_that_disagrees_names_the_member()
    {
        var theShipment = Guid.NewGuid();
        await WhenReceived(new OpenShipment(theShipment, "FedEx"));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await ThenSaga<Shipment>(theShipment, Specify<Shipment>().With(x => x.Delivered, true));
        });

        var then = recording.Steps.Single();
        then.Status.ShouldBe(ResultStatus.failed);
        then.Cells.Single(x => x.Name == nameof(Shipment.Delivered)).Status.ShouldBe(ResultStatus.failed);
        then.Cells.ShouldNotContain(x => x.Name == nameof(Shipment.Carrier));
    }

    [Fact]
    public async Task a_completed_saga_is_gone()
    {
        var theShipment = Guid.NewGuid();
        await GivenSaga<Shipment>(new Shipment { Id = theShipment, Carrier = "UPS" });

        await WhenReceived(new CancelShipment(theShipment));

        var recording = await Recordings.RecordAsync(() => ThenNoSaga<Shipment>(theShipment));
        var then = recording.Steps.Single();
        then.Text.ShouldBe("there is no Shipment saga #theShipment");
        then.Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task a_saga_that_was_never_started_fails_ThenSaga()
    {
        var theShipment = Guid.NewGuid();
        await WhenReceived(new OpenShipment(Guid.NewGuid(), "DHL"));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await Should.ThrowAsync<SpecificationFailedException>(() => ThenSaga<Shipment>(theShipment));
        });

        var then = recording.Steps.Single();
        then.Text.ShouldBe("there is a Shipment saga #theShipment");
        then.Status.ShouldNotBe(ResultStatus.success);
    }
}

[Collection(nameof(MartenShipmentCollection))]
public class saga_state_in_a_document_store(MartenShipmentHost app) : saga_state(app);

[Collection(nameof(LightweightShipmentCollection))]
public class saga_state_in_the_lightweight_saga_tables(LightweightShipmentHost app) : saga_state(app);

[Collection(nameof(InMemoryShipmentCollection))]
public class saga_state_in_memory(InMemoryShipmentHost app) : saga_state(app);
