using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

namespace PostgresqlTests.Sagas;

public class PostgresqlSagaHost : ISagaHost
{
    private IHost _host;

    public IHost BuildHost<TSaga>()
    {
        _host =  Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery().IncludeType<TSaga>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "sagas");
            }).Start();

        return _host;
    }

    public async Task<T> LoadState<T>(Guid id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<PostgresqlMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, Guid>();
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }

    public async Task<T> LoadState<T>(int id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<PostgresqlMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, int>();
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }

    public async Task<T> LoadState<T>(long id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<PostgresqlMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, long>();
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }

    public async Task<T> LoadState<T>(string id) where T : Saga
    {
        var messageStore = _host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<PostgresqlMessageStore>();

        var sagaStorage = messageStore.SagaSchemaFor<T, string>();
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tx = await conn.BeginTransactionAsync();

        var saga = await sagaStorage.LoadAsync(id, tx, CancellationToken.None);
        await conn.CloseAsync();
        return saga;
    }
}

public class basic_mechanics_with_guid : GuidIdentifiedSagaComplianceSpecs<PostgresqlSagaHost>;

public class basic_mechanics_with_int : IntIdentifiedSagaComplianceSpecs<PostgresqlSagaHost>;

public class basic_mechanics_with_long : LongIdentifiedSagaComplianceSpecs<PostgresqlSagaHost>;

public class basic_mechanics_with_string : StringIdentifiedSagaComplianceSpecs<PostgresqlSagaHost>;


