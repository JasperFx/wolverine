using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;

namespace OracleTests;

[Collection("oracle")]
public class inbox_stale_time_compliance : InboxStaleTimeCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync();
    }

    public override async Task BackdateAsync(Envelope envelope, TimeSpan age)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText =
            "UPDATE WOLVERINE.WOLVERINE_INCOMING_ENVELOPES SET \"TIMESTAMP\" = SYSTIMESTAMP AT TIME ZONE 'UTC' - NUMTODSINTERVAL(:secs, 'SECOND') WHERE id = :id";
        cmd.Parameters.Add(new OracleParameter("secs", (int)age.TotalSeconds));
        cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Raw) { Value = envelope.Id.ToByteArray() });
        await cmd.ExecuteNonQueryAsync();
    }
}
