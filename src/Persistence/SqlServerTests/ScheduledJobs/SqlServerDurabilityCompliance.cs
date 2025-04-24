using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using CommandExtensions = Weasel.Core.CommandExtensions;

namespace SqlServerTests.ScheduledJobs;

public class SqlServerDurabilityCompliance : DurabilityComplianceContext<TriggerMessageReceiver, ItemCreatedHandler>
{
    protected override void configureReceiver(WolverineOptions receiverOptions)
    {
        receiverOptions.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "receiver");
    }

    protected override void configureSender(WolverineOptions senderOptions)
    {
        senderOptions.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "sender");
    }

    protected override async Task buildAdditionalObjects()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        await conn.DropSchemaAsync("receiver");
        await conn.CreateSchemaAsync("receiver");

        var table = new Table(new DbObjectName("receiver", "item_created"));
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<string>("name");

        await table.CreateAsync(conn);

        await conn.CloseAsync();
    }

    protected override async Task<ItemCreated> loadItemAsync(IHost receiver, Guid id)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();

        var name = (string)(await conn
            .CreateCommand("select name from receiver.item_created where id = @id")
            .With("id", id)
            .ExecuteScalarAsync());

        if (name.IsEmpty())
        {
            return null;
        }

        return new ItemCreated
        {
            Id = id,
            Name = name
        };
    }

    protected override async Task withContext(IHost sender, MessageContext context,
        Func<MessageContext, ValueTask> action)
    {
        #region sample_basic_sql_server_outbox_sample

        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();

            var tx = conn.BeginTransaction();

            // "context" is an IMessageContext object
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(context, tx));

            await action(context);

            tx.Commit();

            await context.FlushOutgoingMessagesAsync();

            await conn.CloseAsync();
        }

        #endregion
    }

    protected override IReadOnlyList<Envelope> loadAllOutgoingEnvelopes(IHost sender)
    {
        return sender.Get<IMessageStore>().As<SqlServerMessageStore>()
            .Admin.AllOutgoingAsync().GetAwaiter().GetResult();
    }
}

public class TriggerMessageReceiver
{
    [Transactional]
    public ValueTask Handle(TriggerMessage message, IMessageContext context)
    {
        var response = new CascadedMessage
        {
            Name = message.Name
        };

        return context.RespondToSenderAsync(response);
    }
}

#region sample_UsingSqlTransaction

public class ItemCreatedHandler
{
    [Transactional]
    public static async Task Handle(
        ItemCreated created,
        SqlTransaction tx // the current transaction
    )
    {
        // Using some extension method helpers inside of Wolverine here
        await tx.CreateCommand("insert into receiver.item_created (id, name) values (@id, @name)")
            .With("id", created.Id)
            .With("name", created.Name)
            .ExecuteNonQueryAsync();
    }
}

#endregion

public class CreateItemHandler
{
    #region sample_SqlServerOutboxWithSqlTransaction

    [Transactional]
    public async Task<ItemCreatedEvent> Handle(CreateItemCommand command, SqlTransaction tx)
    {
        var item = new Item { Name = command.Name };

        // persist the new Item with the
        // current transaction
        await persist(tx, item);

        return new ItemCreatedEvent { Item = item };
    }

    #endregion

    private Task persist(SqlTransaction tx, Item item)
    {
        // whatever you do to write the new item
        // to your sql server application database
        return Task.CompletedTask;
    }

    public class CreateItemCommand
    {
        public string Name { get; set; }
    }

    public class ItemCreatedEvent
    {
        public Item Item { get; set; }
    }

    public class Item
    {
        public Guid Id;
        public string Name;
    }
}