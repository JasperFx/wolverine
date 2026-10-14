using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

public class inbox_stale_time_compliance : InboxStaleTimeCompliance
{
    public override async Task<IHost> BuildCleanHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "inbox_stale");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync();
    }

    public override async Task BackdateAsync(Envelope envelope, TimeSpan age)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(
                "update inbox_stale.wolverine_incoming_envelopes set [timestamp] = DATEADD(second, -@secs, SYSDATETIMEOFFSET()) where id = @id")
            .With("secs", (int)age.TotalSeconds)
            .With("id", envelope.Id)
            .ExecuteNonQueryAsync();
    }
}
