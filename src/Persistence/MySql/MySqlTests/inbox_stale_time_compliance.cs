using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;

namespace MySqlTests;

[Collection("mysql")]
public class inbox_stale_time_compliance : InboxStaleTimeCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "inbox_stale");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync();
    }

    public override async Task BackdateAsync(Envelope envelope, TimeSpan age)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE inbox_stale.wolverine_incoming_envelopes SET `timestamp` = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @secs SECOND) WHERE id = @id";
        cmd.Parameters.AddWithValue("@secs", (int)age.TotalSeconds);
        cmd.Parameters.AddWithValue("@id", envelope.Id);
        await cmd.ExecuteNonQueryAsync();
    }
}
