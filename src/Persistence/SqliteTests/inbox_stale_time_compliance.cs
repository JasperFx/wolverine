using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

[Collection("sqlite")]
public class inbox_stale_time_compliance : InboxStaleTimeCompliance, IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(inbox_stale_time_compliance));

    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync();
    }

    public override async Task BackdateAsync(Envelope envelope, TimeSpan age)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "update wolverine_incoming_envelopes set \"timestamp\" = datetime('now', @offset) where id = @id";
        cmd.Parameters.AddWithValue("@offset", $"-{(int)age.TotalSeconds} seconds");
        cmd.Parameters.AddWithValue("@id", envelope.Id);
        await cmd.ExecuteNonQueryAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
