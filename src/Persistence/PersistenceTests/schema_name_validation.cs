using Xunit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace PersistenceTests;

/// <summary>
/// GH-3997: a multi-part "schema name" makes every Wolverine table a name with more parts than any
/// engine accepts, and the resulting DDL failures used to be swallowed.
/// </summary>
public class schema_name_validation
{
    [Theory]
    [InlineData("crm.sales.opportunities")]
    [InlineData("crm.sales")]
    // delimiting does not rescue it: the schema is created, but Weasel's CREATE SCHEMA guard compares
    // sys.schemas.name against the bracketed spelling, so every restart re-issues the CREATE and fails
    [InlineData("[crm.sales.opportunities]")]
    [InlineData("\"crm.sales.opportunities\"")]
    [InlineData("`crm.sales.opportunities`")]
    public void reject_a_multi_part_schema_name_on_database_settings(string schemaName)
    {
        var settings = new DatabaseSettings();

        Should.Throw<ArgumentOutOfRangeException>(() => settings.SchemaName = schemaName)
            .Message.ShouldContain(schemaName);
    }

    [Theory]
    [InlineData("wolverine")]
    [InlineData("dbo")]
    [InlineData(null)]
    [InlineData("[quoted_but_single_part]")]
    public void allow_a_usable_schema_name(string? schemaName)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = schemaName
        };

        settings.SchemaName.ShouldBe(schemaName);
    }

    [Fact]
    public void reject_a_multi_part_schema_name_when_configuring_sql_server()
    {
        var options = new WolverineOptions();

        var ex = Should.Throw<ArgumentOutOfRangeException>(() =>
            options.PersistMessagesWithSqlServer("Server=localhost;Database=fake", "crm.sales.opportunities"));

        // the message has to say what to do about it, not just that it is wrong
        ex.Message.ShouldContain("crm.sales.opportunities");
        ex.Message.ShouldContain("'crm'");
    }

    [Fact]
    public void reject_a_multi_part_schema_name_when_configuring_postgresql()
    {
        var options = new WolverineOptions();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            options.PersistMessagesWithPostgresql("Host=localhost;Database=fake", "crm.sales"));
    }

    [Fact]
    public void reject_a_multi_part_transport_schema_name()
    {
        var options = new WolverineOptions();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            options.UseSqlServerPersistenceAndTransport("Server=localhost;Database=fake", "wolverine",
                "crm.sales.queues"));
    }

    [Fact]
    public void migration_failures_are_not_swallowed()
    {
        var logger = new MigrationLogger(NullLogger.Instance);

        using var conn = new SqlConnection("Server=localhost;Database=fake");
        var command = conn.CreateCommand();
        command.CommandText = "create table crm.sales.opportunities.wolverine_incoming_envelopes (id int)";

        var ex = Should.Throw<WolverineSchemaException>(() =>
            logger.OnFailure(command, new DivideByZeroException("boom")));

        ex.Sql.ShouldBe(command.CommandText);
        ex.InnerException.ShouldBeOfType<DivideByZeroException>();
    }
}
