using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Sqlite;
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

    [Fact]
    public void reject_in_memory_connection_string()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PersistMessagesWithSqlite("Data Source=:memory:");
                }).Build();
        });

        ex.Message.ShouldContain("in-memory");
        ex.Message.ShouldContain("file-based");
    }

    [Fact]
    public void reject_memory_mode_connection_string()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseSqlitePersistenceAndTransport("Data Source=wolverine;Mode=Memory;Cache=Shared");
                }).Build();
        });

        ex.Message.ShouldContain("in-memory");
        ex.Message.ShouldContain("file-based");
    }

    [Fact]
    public void reject_in_memory_datasource()
    {
        using var dataSource = new SqliteDataSource("Data Source=wolverine;Mode=Memory;Cache=Shared");

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PersistMessagesWithSqlite(dataSource);
                }).Build();
        });

        ex.Message.ShouldContain("in-memory");
        ex.Message.ShouldContain("dataSource.ConnectionString");
    }

    [Fact]
    public void reject_in_memory_static_tenant_connection_string()
    {
        using var database = Servers.CreateDatabase(nameof(reject_in_memory_static_tenant_connection_string));

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PersistMessagesWithSqlite(database.ConnectionString)
                        .RegisterStaticTenants(tenants =>
                        {
                            tenants.Register("red", "Data Source=:memory:");
                        });
                }).Build();
        });

        ex.Message.ShouldContain("tenant 'red'");
        ex.Message.ShouldContain("file-based");
    }

    [Fact]
    public void reject_in_memory_master_table_seed_connection_string()
    {
        using var database = Servers.CreateDatabase(nameof(reject_in_memory_master_table_seed_connection_string));

        var ex = Should.Throw<InvalidOperationException>(() =>
        {
            using var host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.PersistMessagesWithSqlite(database.ConnectionString)
                        .UseMasterTableTenancy(seed =>
                        {
                            seed.Register("red", "Data Source=:memory:");
                        });
                }).Build();
        });

        ex.Message.ShouldContain("tenant 'red'");
        ex.Message.ShouldContain("file-based");
    }

    [Fact]
    public async Task reject_in_memory_connection_string_from_dynamic_tenant_source()
    {
        using var database = Servers.CreateDatabase(nameof(reject_in_memory_connection_string_from_dynamic_tenant_source));

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlite(database.ConnectionString)
                    .RegisterTenants(new LazyMemoryTenantSource("red"))
                    .EnableMessageTransport(x => x.AutoProvision());
            }).StartAsync();

        var store = host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<MultiTenantedMessageStore>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await store.GetDatabaseAsync("red");
        });

        ex.Message.ShouldContain("tenant connection string");
        ex.Message.ShouldContain("file-based");

        await host.StopAsync();
    }

    private class LazyMemoryTenantSource : ITenantedSource<string>
    {
        private readonly string _tenant;

        public LazyMemoryTenantSource(string tenant)
        {
            _tenant = tenant;
        }

        public DatabaseCardinality Cardinality => DatabaseCardinality.DynamicMultiple;

        public ValueTask<string> FindAsync(string tenantId)
        {
            if (!tenantId.EqualsIgnoreCase(_tenant))
            {
                throw new UnknownTenantIdException(tenantId);
            }

            return ValueTask.FromResult("Data Source=memory-tenant;Mode=Memory;Cache=Shared");
        }

        public Task RefreshAsync()
        {
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> AllActive()
        {
            return Array.Empty<string>();
        }

        public IReadOnlyList<Assignment<string>> AllActiveByTenant()
        {
            return Array.Empty<Assignment<string>>();
        }
    }
}
