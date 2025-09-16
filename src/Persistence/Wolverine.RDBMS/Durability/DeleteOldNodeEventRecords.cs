using System.Data.Common;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class DeleteOldNodeEventRecords : IDatabaseOperation, IDoNotReturnData
{
    private readonly IMessageDatabase _database;
    private readonly DurabilitySettings _settings;

    public DeleteOldNodeEventRecords(IMessageDatabase database, DurabilitySettings settings)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        if (_database.Settings.Role != MessageStoreRole.Main)
        {
            throw new ArgumentOutOfRangeException(nameof(database), "This operation is only valid on 'Main' databases");
        }
        
        _settings = settings;
    }

    public string Description { get; } = "Deleting expired node event records";
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        var cutoffTime = DateTimeOffset.UtcNow.Subtract(_settings.NodeEventRecordExpirationTime);
        
        builder.Append($"delete from {_database.SchemaName}.{DatabaseConstants.NodeRecordTableName} where timestamp < ");
        builder.AppendParameter(cutoffTime);
        builder.Append(';');
    }

    public Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}