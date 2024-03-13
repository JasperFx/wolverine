using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Saga;

public class starting_saga_by_returning_it_from_handler : PostgresqlContext
{
    [Fact]
    public async Task create_sagas_from_a_starting_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Discovery.IncludeType(typeof(StartSagasThing));
                
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var sagaId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartSagas(sagaId));

        var store = host.Services.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var one = await session.LoadAsync<Saga1>(sagaId);
        one.ShouldNotBeNull();
        
        // The cascading messages should have set this
        one.GotOne.ShouldBeTrue();
        
        var two = await session.LoadAsync<Saga2>(sagaId);
        two.ShouldNotBeNull();
        
        // The cascading messages should have set this
        two.GotOne.ShouldBeTrue();

        await host.StopAsync();
        host.Dispose();
    }
}

public record StartSagas(Guid Id);

public static class StartSagasThing
{
    public static (Saga1, Saga2, OutgoingMessages) Handle(StartSagas command)
    {
        var messages = new OutgoingMessages
        {
            new DoOneSaga1(command.Id),
            new DoOneSaga2(command.Id)
        };

        return (new Saga1 { Id = command.Id }, new Saga2 { Id = command.Id }, messages);
    }
}

public class Saga1 : Wolverine.Saga
{
    public Guid Id { get; set; }
    public bool GotOne { get; set; }

    public void Handle(DoOneSaga1 message)
    {
        GotOne = true;
    }
}

public class Saga2 : Wolverine.Saga
{
    public Guid Id { get; set; }
    public bool GotOne { get; set; }
    
    public void Handle(DoOneSaga2 message)
    {
        GotOne = true;
    }
}

public record DoOneSaga1(Guid SagaId);
public record DoOneSaga2(Guid SagaId);
