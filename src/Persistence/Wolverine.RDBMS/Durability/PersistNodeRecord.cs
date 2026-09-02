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
        if (!_events.Any()) throw new InvalidOperationException("PersistNodeRecord should not be used for zero events");

        foreach (var @event in _events)
        {
            // One insert per event, so each is its own statement for the providers that cannot
            // execute several from one command
            builder.StartNewCommand();

            builder.Append("insert into ");

            // GH-2940: emit the schema identifier unquoted, matching every other durability SQL
            // builder in Wolverine.RDBMS (MessageDatabase.{Incoming,Outgoing,Scheduled,Admin,
            // DeadLetterAdminService,ScheduledMessages}.cs all interpolate
            // MessageDatabase.QuotedSchemaName, which is `protected virtual SchemaName` -
            // unquoted). PersistNodeRecord was the lone hold-out using
            // DatabaseSettings.QuotedSchemaName, which hard-codes ANSI double quotes
            // (`"wolverine"`). PostgreSQL and SQL Server accept that, but MySQL/MariaDB under
            // the default sql_mode reject double-quoted identifiers, so node-lifecycle
            // persistence failed with "SQL syntax error... near
            // '\"wolverine\".wolverine_node_records'". Unquoted matches what the rest of the
            // provider already does (and works for every dialect with a default schema name).
            // GH-3943: routed through DatabaseSettings.TableNameFor so SQLite, which has no schemas,
            // gets the prefixed single identifier instead of a `schema.table` qualifier.
            builder.Append(_settings.TableNameFor(DatabaseConstants.NodeRecordTableName));
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