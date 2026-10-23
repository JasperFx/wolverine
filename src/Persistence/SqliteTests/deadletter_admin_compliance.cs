using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

[Collection("sqlite")]
public class deadletter_admin_compliance : DeadLetterAdminCompliance, IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(deadletter_admin_compliance));

    public deadletter_admin_compliance(ITestOutputHelper output) : base(output)
    {
    }

    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
            }).StartAsync();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
