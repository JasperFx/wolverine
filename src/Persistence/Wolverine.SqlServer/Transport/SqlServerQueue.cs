using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Weasel.Core;

namespace Wolverine.SqlServer.Transport;

public class SqlServerQueue : Endpoint, IBrokerQueue
{
    private readonly string _queueTableName;
    private readonly string _scheduledTableName;
    private bool _hasInitialized;

    public SqlServerQueue(string name, SqlServerTransport parent, EndpointRole role = EndpointRole.Application) : base(new Uri($"{SqlServerTransport.ProtocolName}://queue/{name}"), role)
    {
        Parent = parent;
        _queueTableName = $"wolverine_queue_{name}";
        _scheduledTableName = $"wolverine_queue_{name}_scheduled";

        Name = name;
        EndpointName = name;
        
        QueueTable = new QueueTable(Parent.Settings, _queueTableName);
        ScheduledTable = new ScheduledMessageTable(Parent.Settings, _scheduledTableName);
    }

    public string Name { get; }

    internal SqlServerTransport Parent { get; }

    internal Table QueueTable { get; private set; }

    internal Table ScheduledTable { get; private set; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // TODO -- not really implemented here
        var listener = new QueueListener(this, runtime, receiver);
        return ValueTask.FromResult<IListener>(listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        // This gets different when it's a durable vs inline
        return new QueueSender(this);
    }
    
    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        if (Parent.AutoProvision)
        {
            await SetupAsync(logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }

        _hasInitialized = true;
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();

        try
        {
            await conn.CreateCommand($"delete from {QueueTable.Identifier}").ExecuteNonQueryAsync();
            await conn.CreateCommand($"delete from {ScheduledTable.Identifier}").ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var count = await CountAsync();
        var scheduled = await ScheduledCountAsync();

        return new Dictionary<string, string>
            { { "Name", Name }, { "Count", count.ToString() }, { "Scheduled", scheduled.ToString() } };
    }

    public async ValueTask<bool> CheckAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();

        try
        {
            var queueDelta = await QueueTable!.FindDeltaAsync(conn);
            if (queueDelta.HasChanges()) return false;
        
            var scheduledDelta = await ScheduledTable!.FindDeltaAsync(conn);

            return !scheduledDelta.HasChanges();
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();

        await QueueTable!.Drop(conn);
        await ScheduledTable!.Drop(conn);
        
        await conn.CloseAsync();
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();

        await QueueTable!.ApplyChangesAsync(conn);
        await ScheduledTable!.ApplyChangesAsync(conn);
        
        await conn.CloseAsync();
    }

    // TODO -- pull out the string construction of the SQL
    public async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);

        if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            await conn.CreateCommand(
                    $"insert into {ScheduledTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}, {DatabaseConstants.ExecutionTime}) values (@id, @body, @type, @expires, @time)")
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType)
                .With("expires", envelope.DeliverBy)
                .With("time", envelope.ScheduledTime)
                .ExecuteOnce(cancellationToken);
        }
        else
        {
            await conn.CreateCommand(
                    $"insert into {QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) values (@id, @body, @type, @expires)")
                .With("id", envelope.Id)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("type", envelope.MessageType)
                .With("expires", envelope.DeliverBy)
                .ExecuteOnce(cancellationToken);
        }
    }
    
    public async Task MoveFromOutgoingToQueueAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);

        var sql = $@"
DECLARE @NOCOUNT VARCHAR(3) = 'OFF';
IF ( (512 & @@OPTIONS) = 512 ) SET @NOCOUNT = 'ON';
SET NOCOUNT ON;

INSERT into {QueueTable.Identifier} ({DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.KeepUntil}) 
SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}, {DatabaseConstants.MessageType}, {DatabaseConstants.DeliverBy} 
FROM
    {Parent.Settings.SchemaName}.{DatabaseConstants.OutgoingTable} 
WHERE {DatabaseConstants.Id} = @id;
DELETE FROM {Parent.Settings.SchemaName}.{DatabaseConstants.OutgoingTable} WHERE {DatabaseConstants.Id} = @id;

IF (@NOCOUNT = 'ON') SET NOCOUNT ON;
IF (@NOCOUNT = 'OFF') SET NOCOUNT OFF;
";

        await conn.CreateCommand(sql)
            .With("id", envelope.Id)
            .ExecuteOnce(cancellationToken);
    }
    
    // TODO -- will need a separate function for moving out scheduled messages!

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        var sql = $"delete from {QueueTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= SYSDATETIMEOFFSET();delete from {ScheduledTable.Identifier} where {DatabaseConstants.KeepUntil} IS NOT NULL and {DatabaseConstants.KeepUntil} <= SYSDATETIMEOFFSET()";
        await conn.CreateCommand(sql)
            .ExecuteOnce(cancellationToken);
    }


    public async Task<int> CountAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        return (int)await conn.CreateCommand($"select count(*) from {QueueTable.Identifier}").ExecuteScalarAsync();
    }

    public async Task<int> ScheduledCountAsync()
    {
        await using var conn = new SqlConnection(Parent.Settings.ConnectionString);
        await conn.OpenAsync();
        return (int)await conn.CreateCommand($"select count(*) from {ScheduledTable.Identifier}").ExecuteScalarAsync();
    }
    
}