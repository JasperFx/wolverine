using System;
using System.Threading.Tasks;
using IntegrationTests;
using Lamar;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Xunit;

namespace PersistenceTests.Marten;

public class MartenOutbox_end_to_end : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.ConfigureConventionalLocalRouting()
                    .CustomizeQueues((_, q) => q.UseDurableInbox());
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }

    [Fact]
    public async Task persist_and_send_message_one_tx()
    {
        var id = Guid.NewGuid();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();

        var container = (IContainer)_host.Services;
        await using (var nested = container.GetNestedContainer())
        {
            var outbox = nested.GetInstance<IMartenOutbox>();
            var session = nested.GetInstance<IDocumentSession>();
            outbox.Enroll(session);

            session.Store(new Item { Id = id });

            await outbox.PublishAsync(new OutboxedMessage { Id = id });

            await session.SaveChangesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);

        await using var query = container.GetInstance<IDocumentStore>()
            .QuerySession();
        ;
        (await query.LoadAsync<Item>(id)).ShouldNotBeNull();
    }
}

public class Item
{
    public Guid Id { get; set; }
}