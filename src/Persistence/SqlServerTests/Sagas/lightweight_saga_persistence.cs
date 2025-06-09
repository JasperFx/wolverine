using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;

namespace SqlServerTests.Sagas;

public class SqlServerSagaHost : ISagaHost
{
    private IHost _host;

    public IHost BuildHost<TSaga>()
    {
        _host =  Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery().IncludeType<TSaga>();

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "sagas");
            }).Start();

        return _host;
    }

    public async Task<T> LoadState<T>(Guid id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<SqlServerMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, Guid>();
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }

    public async Task<T> LoadState<T>(int id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<SqlServerMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, int>();
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }

    public async Task<T> LoadState<T>(long id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<SqlServerMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, long>();
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }

    public async Task<T> LoadState<T>(string id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<SqlServerMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, string>();
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }
}

public class basic_mechanics_with_guid : GuidIdentifiedSagaComplianceSpecs<SqlServerSagaHost>;

public class basic_mechanics_with_int : IntIdentifiedSagaComplianceSpecs<SqlServerSagaHost>;

public class basic_mechanics_with_long : LongIdentifiedSagaComplianceSpecs<SqlServerSagaHost>;

public class basic_mechanics_with_string : StringIdentifiedSagaComplianceSpecs<SqlServerSagaHost>;


