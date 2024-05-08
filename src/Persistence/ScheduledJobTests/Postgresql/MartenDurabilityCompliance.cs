using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace ScheduledJobTests.Postgresql;

[Collection("marten")]
public class MartenDurabilityCompliance : DurabilityComplianceContext<TriggerMessageReceiver, ItemCreatedHandler>
{
    protected override void configureReceiver(WolverineOptions receiverOptions)
    {
        receiverOptions.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "outbox_receiver";
        }).IntegrateWithWolverine();
    }

    protected override void configureSender(WolverineOptions senderOptions)
    {
        senderOptions.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "outbox_sender";
        }).IntegrateWithWolverine();
    }

    protected override ItemCreated loadItem(IHost receiver, Guid id)
    {
        using (var session = receiver.Get<IDocumentStore>().QuerySession())
        {
            return session.Load<ItemCreated>(id);
        }
    }

    protected override async Task withContext(IHost sender, MessageContext context,
        Func<MessageContext, ValueTask> action)
    {
        var outbox = sender.Get<IMartenOutbox>();

        await action(context);

        await outbox.Session.SaveChangesAsync();
    }

    protected override IReadOnlyList<Envelope> loadAllOutgoingEnvelopes(IHost sender)
    {
        var admin = sender.Get<IMessageStore>().Admin;

        return admin.AllOutgoingAsync().GetAwaiter().GetResult();
    }
}

public class TriggerMessageReceiver
{
    [Transactional]
    public ValueTask Handle(TriggerMessage message, IDocumentSession session, IMessageContext context)
    {
        var response = new CascadedMessage
        {
            Name = message.Name
        };

        return context.RespondToSenderAsync(response);
    }
}

public class ItemCreatedHandler
{
    [Transactional]
    public static void Handle(ItemCreated created, IDocumentSession session,
        Envelope envelope)
    {
        session.Store(created);
    }
}