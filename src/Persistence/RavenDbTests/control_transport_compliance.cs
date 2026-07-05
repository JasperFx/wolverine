using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.RavenDb;
using Wolverine.Runtime;
using Xunit;

namespace RavenDbTests;

public class RavenDbControlTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    private DatabaseFixture _databases = null!;
    private IDocumentStore _store = null!;

    public RavenDbControlTransportFixture() : base(new Uri("ravendb://placeholder"), 30)
    {
        // The RavenDb control queue only registers its NodeControlEndpoint under
        // Balanced durability, so the compliance hosts must run Balanced.
        Mode = DurabilityMode.Balanced;

        // Control messages are transient and each test tracks its own envelopes;
        // resetting the shared store between every test would wipe the running
        // nodes' identity/leadership records out from under them.
        MustReset = false;
    }

    public async Task InitializeAsync()
    {
        _databases = new DatabaseFixture();
        _store = _databases.StartRavenStore();

        await ReceiverIs(opts =>
        {
            opts.Services.AddSingleton(_store);
            opts.UseRavenDbPersistence();
            tightenClusterCadence(opts);
        });

        var receiverNodeId = Receiver.Services.GetRequiredService<IWolverineRuntime>().Options.UniqueNodeId;
        OutboundAddress = new Uri($"ravendb://{receiverNodeId}");

        await SenderIs(opts =>
        {
            opts.Services.AddSingleton(_store);
            opts.UseRavenDbPersistence();
            tightenClusterCadence(opts);
        });
    }

    // The compliance battery runs against a freshly-started two-node Balanced
    // cluster. With the production defaults (CheckAssignmentPeriod 30s,
    // ScheduledJobPollingTime 5s) the leader-assigned scheduled-job agent may not
    // even be assigned inside a single test's timeout — which is what makes
    // schedule_send flap. Tighten the coordination cadence so agent assignment and
    // scheduled-message release happen promptly, mirroring the leadership suite.
    private static void tightenClusterCadence(WolverineOptions opts)
    {
        opts.Durability.CheckAssignmentPeriod = 1.Seconds();
        opts.Durability.HealthCheckPollingTime = 1.Seconds();
        opts.Durability.ScheduledJobPollingTime = 1.Seconds();
        opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
    }

    protected override Task AfterDisposeAsync()
    {
        _store?.Dispose();
        _databases?.Dispose();
        return Task.CompletedTask;
    }

    // Satisfy IAsyncLifetime; real teardown (stopping hosts + store cleanup) runs
    // through the base IAsyncDisposable.DisposeAsync/AfterDisposeAsync path.
    public new Task DisposeAsync() => Task.CompletedTask;
}

[Collection("raven")]
public class control_transport_compliance : TransportCompliance<RavenDbControlTransportFixture>;
