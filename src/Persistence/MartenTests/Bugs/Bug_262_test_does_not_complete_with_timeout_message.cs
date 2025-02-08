using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class saga_cannot_access_stream_just_persisted_in_immediate_timeout : PostgresqlContext
{
    [Fact]
    public async Task should_work_but_doesnt()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                    })
                    .EventForwardingToWolverine()
                    .IntegrateWithWolverine();

                services.AddResourceSetupOnStartup();
            })
            .UseWolverine(w =>
            {
                w.Policies.AutoApplyTransactions();

                // Uncommenting this makes the test hang and probably turn red.
                // Without UseDurableLocalQueues it's green.
                w.Policies.UseDurableLocalQueues();
            })
            .StartAsync();

        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new SomeCommand(id));

        using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        var saga = await session.LoadAsync<SomeSaga>(id);
        saga.ShouldNotBeNull();
        saga.TimedOut.ShouldBeTrue();
    }
}

public record SomeCommand(Guid Id);

public record CreatedEventStartsASaga(Guid Id);

public record SomeStream(Guid Id)
{
    public static SomeStream Create(CreatedEventStartsASaga ev)
    {
        return new(ev.Id);
    }
}

public class SomeSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public bool TimedOut { get; set; }

    public async Task Start(CreatedEventStartsASaga ev, IMessageContext context, IDocumentSession session)
    {
        Id = ev.Id;

        var stream = await session.Events.FetchForWriting<SomeStream>(ev.Id);
        // This does not fail in my app when UseDurableLocalQueues is enabled.
        stream.Aggregate.ShouldNotBeNull();

        // Request a timeout based on some logic, might happen that is set to
        // immediately timeout.
        await context.SendAsync(new SomeTimeout(Id),
            new DeliveryOptions { ScheduledTime = DateTimeOffset.MinValue });
    }

    public async Task Handle(SomeTimeout timeout,
        IDocumentSession session,
        CancellationToken ct)
    {
        var stream = await session.Events.FetchForWriting<SomeStream>(timeout.Id, ct);
        // This fails in my app when UseDurableLocalQueues is enabled.
        stream.Aggregate.ShouldNotBeNull();

        TimedOut = true;
    }
}

public record SomeTimeout(Guid Id);

public class Handler
{
    public static async Task Handle(SomeCommand command,
        IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<SomeStream>(command.Id);
        stream.AppendOne(new CreatedEventStartsASaga(command.Id));
    }
}