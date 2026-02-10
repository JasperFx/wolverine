using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests;

public class configuration_extension_methods : SqliteContext
{
    [Fact]
    public void default_schema_name_is_main()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(Servers.CreateInMemoryConnectionString());
            }).Build();

        var settings = host.Services.GetRequiredService<DatabaseSettings>();
        settings.SchemaName.ShouldBe("main");
    }

    [Fact]
    public void can_override_schema_name()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite("Data Source=:memory:", "custom");
            }).Build();

        var settings = host.Services.GetRequiredService<DatabaseSettings>();
        settings.SchemaName.ShouldBe("custom");
    }

    [Fact]
    public void default_scheduled_job_lock_id()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(Servers.CreateInMemoryConnectionString());
            }).Build();

        var settings = host.Services.GetRequiredService<DatabaseSettings>();
        settings.ScheduledJobLockId.ShouldBe("main:scheduled-jobs".GetDeterministicHashCode());
    }
}
