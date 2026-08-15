using Shouldly;
using Wolverine.RDBMS;
using Xunit;

namespace PersistenceTests;

/// <summary>
/// GH-3943 introduced a single qualification point — <see cref="DatabaseSettings.TableNameFor"/> and
/// its <c>MessageDatabase&lt;T&gt;</c> twin — that every reference to a Wolverine table in generated
/// SQL now routes through, so that SQLite can render a table name prefix where the engines that
/// actually have schemas render a <c>schema.table</c> qualifier.
///
/// <para>
/// These are the guard rails on that hook: the default rendering has to stay byte-identical to the
/// interpolation it replaced, because Postgres, SQL Server, MySQL and Oracle all reach it.
/// </para>
/// </summary>
public class table_naming_contract
{
    [Fact]
    public void qualifies_with_the_schema_by_default()
    {
        var settings = new DatabaseSettings { SchemaName = "wolverine" };

        settings.TableNameFor(DatabaseConstants.IncomingTable)
            .ShouldBe("wolverine.wolverine_incoming_envelopes");
        settings.QuotedTableNameFor(DatabaseConstants.IncomingTable)
            .ShouldBe("\"wolverine\".wolverine_incoming_envelopes");
    }

    [Fact]
    public void no_schema_name_means_no_qualifier()
    {
        // PersistNodeRecord guarded on this explicitly before GH-3943 and still depends on it.
        var settings = new DatabaseSettings { SchemaName = null };

        settings.TableNameFor(DatabaseConstants.NodeRecordTableName)
            .ShouldBe(DatabaseConstants.NodeRecordTableName);
        settings.QuotedTableNameFor(DatabaseConstants.NodeRecordTableName)
            .ShouldBe(DatabaseConstants.NodeRecordTableName);
    }

    [Fact]
    public void prefixes_rather_than_qualifies_when_the_engine_has_no_schemas()
    {
        var settings = new DatabaseSettings { SchemaName = "reporting", SchemaNameIsTablePrefix = true };

        settings.TableNameFor(DatabaseConstants.IncomingTable)
            .ShouldBe("reporting_wolverine_incoming_envelopes");

        // A prefixed name is a single identifier, so there is nothing for the quoting to bracket.
        settings.QuotedTableNameFor(DatabaseConstants.IncomingTable)
            .ShouldBe("reporting_wolverine_incoming_envelopes");
    }

    [Theory]
    [InlineData(null, "wolverine_incoming_envelopes")]
    [InlineData("", "wolverine_incoming_envelopes")]
    // "main" is Wolverine.Sqlite's default and the only schema a plain SQLite connection always
    // knows. It has to prefix nothing, or every database provisioned before GH-3943 is orphaned.
    [InlineData("main", "wolverine_incoming_envelopes")]
    [InlineData("custom", "custom_wolverine_incoming_envelopes")]
    public void prefixing_rules(string? schemaName, string expected)
    {
        TablePrefixing.Apply(schemaName, DatabaseConstants.IncomingTable).ShouldBe(expected);
    }
}
