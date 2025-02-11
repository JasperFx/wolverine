using JasperFx.Core.Reflection;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Transports;

namespace MartenTests.MultiTenancy;

public class basic_bootstrapping_and_database_configuration : MultiTenancyContext
{
    public basic_bootstrapping_and_database_configuration(MultiTenancyFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void bootstrapped_at_all()
    {
        true.ShouldBeTrue();
    }

    [Fact]
    public void should_have_the_specified_master_database_as_master()
    {
        Databases.Master.Name.ShouldBe("Master");
        Databases.Master.As<IMessageDatabase>().SchemaName.ShouldBe("control");

        new NpgsqlConnectionStringBuilder(Databases.Master.As<IMessageDatabase>().DataSource.CreateConnection().ConnectionString)
            .Database.ShouldBe("postgres");
    }

    [Fact]
    public void knows_about_tenant_databases()
    {
        // 3 tenant databases
        Databases.ActiveDatabases().Count.ShouldBe(4);

        Databases.ActiveDatabases().ShouldContain(x => x.Name == "tenant1");
        Databases.ActiveDatabases().ShouldContain(x => x.Name == "tenant2");
        Databases.ActiveDatabases().ShouldContain(x => x.Name == "tenant3");
    }

    [Fact]
    public async Task tenant_databases_have_envelope_tables()
    {
        foreach (var database in Databases.ActiveDatabases().OfType<IMessageDatabase>().Where(x => x.Name != "Master"))
        {
            await using var conn = (NpgsqlConnection)await database.DataSource.OpenConnectionAsync();

            var tables = (await conn.ExistingTablesAsync()).ToArray();

            tables = tables.Where(x => x.Schema == "control").ToArray();
            tables.ShouldContain(x => x.Name == DatabaseConstants.IncomingTable);
            tables.ShouldContain(x => x.Name == DatabaseConstants.OutgoingTable);
            tables.ShouldContain(x => x.Name == DatabaseConstants.DeadLetterTable);

            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task tenant_databases_do_not_have_node_and_assignment_tables()
    {
        foreach (var database in Databases.ActiveDatabases().OfType<IMessageDatabase>().Where(x => x.Name != "Master"))
        {
            await using var conn = (NpgsqlConnection)await database.DataSource.OpenConnectionAsync();

            var tables = (await conn.ExistingTablesAsync()).Where(x => x.Schema == "mt").ToArray();
            tables.ShouldNotContain(x => x.Name == DatabaseConstants.NodeTableName);
            tables.ShouldNotContain(x => x.Name == DatabaseConstants.NodeAssignmentsTableName);
            tables.ShouldNotContain(x => x.Name == DatabaseConstants.ControlQueueTableName);

            await conn.CloseAsync();
        }
    }

    [Fact]
    public async Task finds_database_for_default_is_master()
    {
        (await Databases.GetDatabaseAsync(TransportConstants.Default)).ShouldBeSameAs(Databases.Master);
    }

    [Fact]
    public async Task master_database_has_every_storage_table()
    {
        await using var conn = (NpgsqlConnection)await Databases.Master.As<IMessageDatabase>().DataSource.OpenConnectionAsync();

        var tables = (await conn.ExistingTablesAsync()).Where(x => x.Schema == "control").ToArray();
        tables.ShouldContain(x => x.Name == DatabaseConstants.IncomingTable);
        tables.ShouldContain(x => x.Name == DatabaseConstants.OutgoingTable);
        tables.ShouldContain(x => x.Name == DatabaseConstants.DeadLetterTable);

        // Master only tables
        tables.ShouldContain(x => x.Name == DatabaseConstants.NodeTableName);
        tables.ShouldContain(x => x.Name == DatabaseConstants.NodeAssignmentsTableName);
        tables.ShouldContain(x => x.Name == DatabaseConstants.ControlQueueTableName);

        await conn.CloseAsync();
    }

    [Fact]
    public void only_the_master_database_is_the_master()
    {
        foreach (var database in Databases.ActiveDatabases().OfType<IMessageDatabase>().Where(x => x.Name != "Master"))
        {
            database.IsMaster.ShouldBeFalse();
        }

        Databases.Master.As<IMessageDatabase>().IsMaster.ShouldBeTrue();
    }
}