using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;

namespace SqliteTests;

public class configuration_extension_methods : SqliteContext
{
    [Fact]
    public void default_schema_name_is_main()
    {
        using var database = Servers.CreateDatabase(nameof(default_schema_name_is_main));
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(database.ConnectionString);
            }).Build();

        var settings = host.Services.GetRequiredService<DatabaseSettings>();
        settings.SchemaName.ShouldBe("main");
    }

    [Fact]
    public void can_override_schema_name()
    {
        using var database = Servers.CreateDatabase(nameof(can_override_schema_name));
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(database.ConnectionString, "custom");
            }).Build();

        var settings = host.Services.GetRequiredService<DatabaseSettings>();
        settings.SchemaName.ShouldBe("custom");
    }

    [Fact]
    public void default_scheduled_job_lock_id()
    {
        using var database = Servers.CreateDatabase(nameof(default_scheduled_job_lock_id));
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(database.ConnectionString);
            }).Build();

        var settings = host.Services.GetRequiredService<DatabaseSettings>();
        settings.ScheduledJobLockId.ShouldBe("main:scheduled-jobs".GetDeterministicHashCode());
    }

    [Fact]
    public void use_sqlite_persistence_and_transport_has_single_connection_string_overload()
    {
        var methods = typeof(SqliteConfigurationExtensions)
            .GetMethods()
            .Where(x => x.Name == nameof(SqliteConfigurationExtensions.UseSqlitePersistenceAndTransport))
            .ToArray();

        methods.Length.ShouldBe(1);

        var parameters = methods.Single().GetParameters();
        parameters.Length.ShouldBe(2);
        parameters[1].ParameterType.ShouldBe(typeof(string));
    }

    [Fact]
    public void sqlite_transport_expression_no_longer_exposes_schema_setters()
    {
        var methods = typeof(SqlitePersistenceExpression)
            .GetMethods()
            .Select(x => x.Name)
            .ToArray();

        methods.ShouldNotContain("TransportSchemaName");
        methods.ShouldNotContain("MessageStorageSchemaName");
    }
}
