using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Core;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class inbox_stale_time_compliance : InboxStaleTimeCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "inbox_stale");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync();
    }

    public override async Task BackdateAsync(Envelope envelope, TimeSpan age)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(
                "update inbox_stale.wolverine_incoming_envelopes set \"timestamp\" = now() - make_interval(secs => :secs::int) where id = :id")
            .With("secs", (int)age.TotalSeconds)
            .With("id", envelope.Id)
            .ExecuteNonQueryAsync();
    }
}
