using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;

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

        options.Services.AddTransient<IMessageStore, PostgresqlMessageStore>();
        options.Services.AddSingleton(s => (IDatabase)s.GetRequiredService<IMessageStore>());
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());

        options.Services.For<NpgsqlConnection>().Use<NpgsqlConnection>();

        options.Services.Add(new ServiceDescriptor(typeof(NpgsqlConnection),
            new NpgsqlConnectionInstance(typeof(NpgsqlConnection))));
        options.Services.Add(new ServiceDescriptor(typeof(DbConnection),
            new NpgsqlConnectionInstance(typeof(DbConnection))));

        options.CodeGeneration.AddPersistenceStrategy<PostgresqlPersistenceFrameProvider>();
    }
}