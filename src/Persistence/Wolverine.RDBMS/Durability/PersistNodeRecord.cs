using System.Data.Common;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

public class PersistNodeRecord : IDatabaseOperation, IDoNotReturnData
{
    private readonly string _tableName;
    private readonly NodeRecord[] _events;

    public PersistNodeRecord(DatabaseSettings settings, NodeRecord[] events)
        : this(settings.TableNameFor(DatabaseConstants.NodeRecordTableName), events)
    {
    }

    /// <param name="tableName">The node record table, already rendered for the engine's SQL dialect</param>
    public PersistNodeRecord(string tableName, NodeRecord[] events)
    {
        _tableName = tableName;
        _events = events;
    }

    public string Description => nameof(PersistNodeRecord);
    public void ConfigureCommand(DbCommandBuilder builder)
    {
        if (!_events.Any()) throw new InvalidOperationException("PersistNodeRecord should not be used for zero events");

        foreach (var @event in _events)
        {
            // One insert per event, so each is its own statement for the providers that cannot
            // execute several from one command
            builder.StartNewCommand();

            builder.Append("insert into ");

            // GH-2940: rendered by the caller, because MySQL rejects the ANSI-quoted schema that
            // PostgreSQL needs for a reserved-word schema name
            builder.Append(_tableName);
            builder.Append(" (node_number, event_name, description) values (");
            builder.AppendParameter(@event.NodeNumber);
            builder.Append(", ");
            builder.AppendParameter(@event.RecordType.ToString());
            builder.Append(", ");

            // GH-4246: clamped to the width every bounded description column now declares. An
            // AssignmentChanged record's description is an agent command's ToString(), which grows with
            // the agent URI, the schema name and the destination node -- long enough on a real cluster to
            // overflow the column and fail this insert, which fails the whole AgentCommand batch behind
            // it. These rows are diagnostics; losing a tail beats losing an assignment.
            builder.AppendParameter(NodeRecord.TruncateDescription(@event.Description));
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