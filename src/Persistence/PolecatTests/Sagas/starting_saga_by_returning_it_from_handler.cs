using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.Sagas;

public class starting_saga_by_returning_it_from_handler
{
    [Fact]
    public async Task create_sagas_from_a_starting_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "start_saga_return";
                }).IntegrateWithWolverine();

                opts.Discovery.IncludeType(typeof(StartSagasThing));

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        var sagaId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartSagas(sagaId));

        await using var session = store.LightweightSession();

        var one = await session.LoadAsync<PcSaga1>(sagaId);
        one.ShouldNotBeNull();

        // The cascading messages should have set this
        one.GotOne.ShouldBeTrue();

        var two = await session.LoadAsync<PcSaga2>(sagaId);
        two.ShouldNotBeNull();

        // The cascading messages should have set this
        two.GotOne.ShouldBeTrue();

        await host.StopAsync();
    }
}

public record StartSagas(Guid Id);

public static class StartSagasThing
{
    public static (PcSaga1, PcSaga2, OutgoingMessages) Handle(StartSagas command)
    {
        var messages = new OutgoingMessages
        {
            new DoOnePcSaga1(command.Id),
            new DoOnePcSaga2(command.Id)
        };

        return (new PcSaga1 { Id = command.Id }, new PcSaga2 { Id = command.Id }, messages);
    }
}

public class PcSaga1 : Wolverine.Saga
{
    public Guid Id { get; set; }
    public bool GotOne { get; set; }

    public void Handle(DoOnePcSaga1 message)
    {
        GotOne = true;
    }
}

public class PcSaga2 : Wolverine.Saga
{
    public Guid Id { get; set; }
    public bool GotOne { get; set; }

    public void Handle(DoOnePcSaga2 message)
    {
        GotOne = true;
    }
}

public record DoOnePcSaga1(Guid SagaId);
public record DoOnePcSaga2(Guid SagaId);
