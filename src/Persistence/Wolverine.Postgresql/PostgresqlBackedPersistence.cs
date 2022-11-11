using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Weasel.Core.Migrations;
using Wolverine.RDBMS;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Postgresql;

/// <summary>
///     Activates the Sql Server backed message persistence
/// </summary>
internal class PostgresqlBackedPersistence : IWolverineExtension
{
    public PostgresqlSettings Settings { get; } = new();

    public void Configure(WolverineOptions options)
    {
        options.Services.AddSingleton(Settings);

        options.Services.AddTransient<IMessageStore, PostgresqlMessageStore>();
        options.Services.AddSingleton(s => (IDatabase)s.GetRequiredService<IMessageStore>());
        options.Advanced.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());

        options.Services.For<NpgsqlConnection>().Use<NpgsqlConnection>();

        options.Services.Add(new ServiceDescriptor(typeof(NpgsqlConnection),
            new NpgsqlConnectionInstance(typeof(NpgsqlConnection))));
        options.Services.Add(new ServiceDescriptor(typeof(DbConnection),
            new NpgsqlConnectionInstance(typeof(DbConnection))));

        options.Advanced.CodeGeneration.SetTransactionsIfNone(new PostgresqlTransactionFrameProvider());
    }
}
