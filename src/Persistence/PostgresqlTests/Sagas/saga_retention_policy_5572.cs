using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace PostgresqlTests.Sagas;

public record StartPolicySaga(string Id);

public record TouchPolicySaga(string Id);

public record FinishPolicySaga(string Id);

public class PolicySaga : Saga
{
    public string Id { get; set; } = string.Empty;

    public static PolicySaga Start(StartPolicySaga command) => new() { Id = command.Id };

    public void Handle(TouchPolicySaga command)
    {
    }

    public void Handle(FinishPolicySaga command) => MarkCompleted();
}

public record ShipmentRequested(string ShipmentId);

public record ShipmentDelivered(string ShipmentId);

#region sample_retain_when_completed

// Keep each shipment for two days after it completes
[RetainWhenCompleted(Days = 2)]
public class ShipmentSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public bool Delivered { get; set; }

    public static ShipmentSaga Start(ShipmentRequested command) => new() { Id = command.ShipmentId };

    public void Handle(ShipmentDelivered command)
    {
        Delivered = true;
        MarkCompleted();
    }

    // A message for a shipment that already completed comes here, never to Handle()
    public static void NotFound(ShipmentDelivered command, ILogger<ShipmentSaga> logger)
    {
        logger.LogInformation("Shipment {Id} was already delivered", command.ShipmentId);
    }
}

#endregion

// GH-5572: [RetainWhenCompleted] for one saga type, opts.Policies.RetainCompletedSagas() for every saga type, and
// nothing changes without either
public class saga_retention_policy_5572
{
    private static async Task<IHost> startAsync(bool retain)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PolicySaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AddSagaType<PolicySaga>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_retention_policy");

                if (retain)
                {
                    #region sample_retain_completed_sagas_policy

                    // Keep every completed saga for 3 days before deleting it
                    opts.Policies.RetainCompletedSagas(3.Days());

                    #endregion
                }
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> startAndFinishAsync(IHost host)
    {
        var id = Guid.NewGuid().ToString();
        await host.InvokeMessageAndWaitAsync(new StartPolicySaga(id));
        await host.InvokeMessageAndWaitAsync(new FinishPolicySaga(id));
        return id;
    }

    [Fact]
    public async Task the_policy_retains_completed_sagas()
    {
        using var host = await startAsync(retain: true);
        var id = await startAndFinishAsync(host);

        var state = await host.GetRuntime().SagaStorage.ReadSagaAsync(typeof(PolicySaga).FullName!, id,
            TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();

        host.GetRuntime().Handlers.ChainFor<FinishPolicySaga>().ShouldBeOfType<SagaChain>()
            .CompletedSagaRetention.ShouldBe(3.Days());

        await Should.ThrowAsync<UnknownSagaException>(() =>
            host.MessageBus().InvokeAsync(new TouchPolicySaga(id), TestContext.Current.CancellationToken));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_attribute_retains_completed_sagas_of_that_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ShipmentSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.AddSagaType<ShipmentSaga>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_retention_policy");
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid().ToString();
        await host.InvokeMessageAndWaitAsync(new ShipmentRequested(id));
        await host.InvokeMessageAndWaitAsync(new ShipmentDelivered(id));

        // Delivered again: goes to NotFound, and the retained saga is left alone
        await host.InvokeMessageAndWaitAsync(new ShipmentDelivered(id));

        var state = await host.GetRuntime().SagaStorage.ReadSagaAsync(typeof(ShipmentSaga).FullName!, id,
            TestContext.Current.CancellationToken);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();

        host.GetRuntime().Handlers.ChainFor<ShipmentDelivered>().ShouldBeOfType<SagaChain>()
            .CompletedSagaRetention.ShouldBe(2.Days());

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task without_retention_a_completed_saga_is_deleted_as_before()
    {
        using var host = await startAsync(retain: false);
        var id = await startAndFinishAsync(host);

        (await host.GetRuntime().SagaStorage.ReadSagaAsync(typeof(PolicySaga).FullName!, id,
            TestContext.Current.CancellationToken)).ShouldBeNull();

        var chain = host.GetRuntime().Handlers.ChainFor<FinishPolicySaga>().ShouldBeOfType<SagaChain>();
        chain.CompletedSagaRetention.ShouldBeNull();
        chain.SourceCode!.ShouldNotContain(nameof(SagaRetention));

        host.GetRuntime().Agents.AllRunningAgentUris()
            .ShouldNotContain(x => x.Scheme == "wolverine-saga-retention");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
