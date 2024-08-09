using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace Wolverine.Postgresql;

/// <summary>
///     Activates the Postgresql backed message persistence
/// </summary>
internal class PostgresqlBackedPersistence : IWolverineExtension
{
    public DatabaseSettings Settings { get; } = new()
    {
        IsMaster = true
    };

    public void Configure(WolverineOptions options)
    {
        options.Services.AddSingleton(Settings);

        options.Services.TryAddSingleton<NpgsqlDataSource>(s => (NpgsqlDataSource)Settings.DataSource! ?? NpgsqlDataSource.Create(Settings.ConnectionString!));

        options.Services.AddTransient<IMessageStore, PostgresqlMessageStore>();
        options.Services.AddSingleton(s => (IDatabase)s.GetRequiredService<IMessageStore>());
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());

        options.Services.AddScoped<NpgsqlConnection, NpgsqlConnection>();

        options.CodeGeneration.Sources.Add(new NpgsqlConnectionSource());

        options.CodeGeneration.AddPersistenceStrategy<PostgresqlPersistenceFrameProvider>();
        
        options.Services.AddSingleton<IDatabaseSagaStorage>(s => (IDatabaseSagaStorage)s.GetRequiredService<IMessageStore>());
    }
}