using MySqlConnector;
using Shouldly;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace MySqlTests;

// Regression for GH-2940. PersistNodeRecord.ConfigureCommand used to interpolate
// DatabaseSettings.QuotedSchemaName, which hard-codes ANSI double quotes around the schema name
// (`"wolverine"`). MySQL/MariaDB under the default sql_mode reject double-quoted identifiers
// (they expect backticks), so node-lifecycle persistence failed with a SQL syntax error every
// time. This was the lone hold-out using QuotedSchemaName - every other durability SQL builder
// in Wolverine.RDBMS uses the (unquoted) MessageDatabase.QuotedSchemaName, which is just
// SchemaName.
//
// The test inspects the generated SQL string directly so it runs without MySQL connectivity
// (only the DbCommand instance type pins the dialect, and a "wolverine" schema name is a valid
// unquoted identifier in every provider Wolverine supports).
public class Bug_2940_persist_node_record_unquoted_schema
{
    [Fact]
    public void persist_node_record_does_not_wrap_schema_name_in_ansi_double_quotes()
    {
        var settings = new DatabaseSettings { SchemaName = "wolverine" };
        var op = new PersistNodeRecord(settings,
        [
            new NodeRecord
            {
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStarted,
                Description = "test"
            }
        ]);

        var builder = new DbCommandBuilder(new MySqlCommand());
        op.ConfigureCommand(builder);

        var sql = builder.ToString();

        // The exact bug surface: MySQL parser saw `"wolverine"` and rejected it.
        sql.ShouldNotContain("\"wolverine\"");
        sql.ShouldContain("wolverine.wolverine_node_records");
    }

    [Fact]
    public void persist_node_record_with_null_schema_name_emits_unprefixed_table()
    {
        // Defensive: DatabaseSettings.SchemaName is nullable. The original
        // QuotedSchemaName getter returned "" for null, producing `.wolverine_node_records`
        // (a stray leading dot - also a syntax error). Verify the unquoted path skips the
        // schema prefix entirely when the schema name is absent.
        var settings = new DatabaseSettings { SchemaName = null };
        var op = new PersistNodeRecord(settings,
        [
            new NodeRecord
            {
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStarted,
                Description = "test"
            }
        ]);

        var builder = new DbCommandBuilder(new MySqlCommand());
        op.ConfigureCommand(builder);

        var sql = builder.ToString();

        sql.ShouldNotContain(".wolverine_node_records");
        sql.ShouldContain("insert into wolverine_node_records");
    }
}
