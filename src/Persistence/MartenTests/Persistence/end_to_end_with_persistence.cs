using IntegrationTests;
using JasperFx.Core;
using Marten;
using MartenTests.Persistence.Resiliency;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace MartenTests.Persistence;

public class end_to_end_with_persistence : PostgresqlContext, IDisposable, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost theReceiver = null!;

    private IHost theSender = null!;

    public end_to_end_with_persistence(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        theSender = await WolverineHost.ForAsync(opts =>
        {
            opts.Publish(x =>
            {
                x.Message<ItemCreated>();
                x.Message<Question>();
                x.ToPort(2345).UseDurableOutbox();
            });

            opts.Services.AddMarten(x =>
            {
                x.DisableNpgsqlLogging = true;
                x.Connection(Servers.PostgresConnectionString);
                x.DatabaseSchemaName = "sender";
            }).IntegrateWithWolverine();

            opts.ListenAtPort(2567);
        });

        theReceiver = await WolverineHost.ForAsync(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "receiver");

            opts.ListenAtPort(2345).UseDurableInbox();

            opts.Services.AddMarten(x =>
            {
                x.DisableNpgsqlLogging = true;
                x.Connection(Servers.PostgresConnectionString);
                x.DatabaseSchemaName = "receiver";
            }).IntegrateWithWolverine();
        });

        await theSender.ResetResourceState();
        await theReceiver.ResetResourceState();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        theSender?.Dispose();
        theReceiver?.Dispose();
    }

    [Fact]
    public async Task delete_all_persisted_envelopes()
    {
        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };


        await theSender.MessageBus().ScheduleAsync(item, 1.Days());

        var persistor = theSender.Get<IMessageStore>();

        var counts = await persistor.Admin.FetchCountsAsync();

        counts.Scheduled.ShouldBe(1);

        await persistor.Admin.ClearAllAsync();

        (await persistor.Admin.FetchCountsAsync()).Scheduled.ShouldBe(0);
    }

    [Fact]
    public async Task publish_locally()
    {
        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };

        await theReceiver.ExecuteAndWaitValueTaskAsync(c => c.PublishAsync(item));


        var documentStore = theReceiver.Get<IDocumentStore>();
        await using (var session = documentStore.QuerySession())
        {
            var item2 = await session.LoadAsync<ItemCreated>(item.Id);
            if (item2 == null)
            {
                Thread.Sleep(500);
                item2 = await session.LoadAsync<ItemCreated>(item.Id);
            }

            item2!.Name.ShouldBe("Shoe");
        }

        var incoming = await theReceiver.Get<IMessageStore>().Admin.FetchCountsAsync();
        incoming.Incoming.ShouldBe(0);
    }
}