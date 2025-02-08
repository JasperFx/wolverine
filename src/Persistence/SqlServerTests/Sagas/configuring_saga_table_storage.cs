using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.SqlServer;

namespace SqlServerTests.Sagas;

public class configuring_saga_table_storage : SqlServerContext
{
    [Fact]
    public async Task add_tables_to_persistence()
    {
        await dropSchemaAsync();

        #region sample_manually_adding_saga_types

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.AddSagaType<RedSaga>("red");
                opts.AddSagaType(typeof(BlueSaga),"blue");
                
                
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "color_sagas");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

            #endregion
        
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        (await new Table(new DbObjectName("color_sagas", "red")).ExistsInDatabaseAsync(conn)).ShouldBeTrue();
        (await new Table(new DbObjectName("color_sagas", "blue")).ExistsInDatabaseAsync(conn)).ShouldBeTrue();
    }

    private static async Task dropSchemaAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("color_sagas");
        await conn.CloseAsync();
    }
}

public class RedSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class BlueSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}