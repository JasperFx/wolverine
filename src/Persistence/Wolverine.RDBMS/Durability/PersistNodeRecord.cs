using System.Data.Common;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

public class PersistNodeRecord : IDatabaseOperation, IDoNotReturnData
{
    private readonly DatabaseSettings _settings;
    private readonly NodeRecord[] _events;

    public PersistNodeRecord(DatabaseSettings settings, NodeRecord[] events)
    {
        _settings = settings;
        _events = events;
    }

    public string Description => nameof(PersistNodeRecord);
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        foreach (var @event in _events)
        {
            builder.Append("insert into ");
            builder.Append(_settings.SchemaName);
            builder.Append(".");
            builder.Append(DatabaseConstants.NodeRecordTableName);
            builder.Append(" (node_number, event_name, description) values (");
            builder.AppendParameter(@event.NodeNumber);
            builder.Append(", ");
            builder.AppendParameter(@event.RecordType.ToString());
            builder.Append(", ");
            builder.AppendParameter(@event.Description);
            builder.Append(");");
        }
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