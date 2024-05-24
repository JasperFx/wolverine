using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests;

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

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task persist_and_send_message_one_tx()
    {
        var id = Guid.NewGuid();

        var waiter = OutboxedMessageHandler.WaitForNextMessage();

        using (var nested = _host.Services.CreateScope())
        {
            var outbox = nested.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var session = nested.ServiceProvider.GetRequiredService<IDocumentSession>();
            outbox.Enroll(session);

            session.Store(new Item { Id = id });

            await outbox.PublishAsync(new OutboxedMessage { Id = id });

            await session.SaveChangesAsync();
        }

        var message = await waiter;
        message.Id.ShouldBe(id);

        await using var query = _host.Services.GetRequiredService<IDocumentStore>()
            .QuerySession();
        ;
        (await query.LoadAsync<Item>(id)).ShouldNotBeNull();
    }
}

public class Item
{
    public Guid Id { get; set; }
}

public record OutboxedMessage
{
    public Guid Id { get; set; }
}

public class OutboxedMessageHandler
{
    private static TaskCompletionSource<OutboxedMessage> _source;

    public static Task<OutboxedMessage> WaitForNextMessage()
    {
        _source = new TaskCompletionSource<OutboxedMessage>();

        return _source.Task.WaitAsync(15.Seconds());
    }

    public void Handle(OutboxedMessage message)
    {
        _source?.TrySetResult(message);
    }
}