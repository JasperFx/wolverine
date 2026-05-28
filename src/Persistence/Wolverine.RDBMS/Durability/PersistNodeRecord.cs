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
            if (!string.IsNullOrEmpty(_settings.SchemaName))
            {
                builder.Append(_settings.SchemaName);
                builder.Append('.');
            }

            builder.Append(DatabaseConstants.NodeRecordTableName);
            builder.Append(" (node_number, event_name, description) values (");
            builder.AppendParameter(@event.NodeNumber);
            builder.Append(", ");
            builder.AppendParameter(@event.RecordType.ToString());
            builder.Append(", ");
            builder.AppendParameter(@event.Description!);
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