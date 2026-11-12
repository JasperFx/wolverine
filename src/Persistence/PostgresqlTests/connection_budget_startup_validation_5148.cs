#if NET11_0_OR_GREATER
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;

namespace PostgresqlTests;

// GH-5148. The .NET 11 IAsyncStartupValidator over Durability.ConnectionBudgets.
[Collection("marten")]
public class connection_budget_startup_validation_5148
{
    private const string TheSchema = "budget_validation_5148";

    private static string thePostgresHost()
    {
        return new Npgsql.NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString).Host!;
    }

    private static int thePostgresPort()
    {
        return new Npgsql.NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString).Port;
    }

    [Fact]
    public async Task a_budget_for_a_server_no_store_lives_on_refuses_startup()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Durability.ConnectionBudgets.ForServer("no-such-host-5148", 5432, 100);
            }).Build();

        var ex = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("no-such-host-5148:5432");
        ex.Message.ShouldContain($"{thePostgresHost()}:{thePostgresPort()}");
    }

    [Fact]
    public async Task a_budget_for_the_real_server_starts()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Durability.ConnectionBudgets.ForServer(thePostgresHost(), thePostgresPort(), 10_000);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_already_exhausted_budget_only_warns_and_still_starts()
    {
        // At least the probe's own connection is open, so a budget of 1 is always met at startup.
        // During a rolling deploy that is normal for the newest node, so it must not refuse.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Durability.ConnectionBudgets.ForServer(thePostgresHost(), 1);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task an_explicitly_disabled_budget_machinery_skips_validation()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Durability.ConnectionBudgets.ForServer("no-such-host-5148", 5432, 100);
                opts.Durability.ConnectionBudgets.Enabled = false;
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
#endif
