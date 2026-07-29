using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Tracking;

namespace SqliteTests;

public class extension_registrations : SqliteContext
{
    [Fact]
    public async Task should_register_message_store()
    {
        using var database = Servers.CreateDatabase(nameof(should_register_message_store));
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(database.ConnectionString);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IMessageStore>()
            .ShouldBeOfType<SqliteMessageStore>();
    }

    [Fact]
    public async Task should_set_durability_agent()
    {
        using var database = Servers.CreateDatabase(nameof(should_set_durability_agent));
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(database.ConnectionString);
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        runtime.Storage.ShouldBeOfType<SqliteMessageStore>();
    }
}
