using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Persistence;
using Wolverine.SqlServer.Util;

namespace Wolverine.SqlServer;

/// <summary>
///     Activates the Sql Server backed message persistence
/// </summary>
internal class SqlServerBackedPersistence : IWolverineExtension
{
    public DatabaseSettings Settings { get; } = new(){IsMaster = true};

    public void Configure(WolverineOptions options)
    {
        options.Services.AddSingleton(Settings);

        options.Services.AddTransient<IMessageStore, SqlServerMessageStore>();
        options.Services.AddSingleton(s => (IDatabase)s.GetRequiredService<IMessageStore>());

        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());

        options.Services.AddScoped<SqlConnection, SqlConnection>();

        options.CodeGeneration.Sources.Add(new SqlConnectionSource());

        // Don't overwrite the EF Core transaction support if it's there
        options.CodeGeneration.AddPersistenceStrategy<SqlServerPersistenceFrameProvider>();
    }
}