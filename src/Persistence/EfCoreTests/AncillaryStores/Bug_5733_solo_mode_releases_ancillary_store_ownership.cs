using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Transports.Tcp;

namespace EfCoreTests.AncillaryStores;

// GH-5733. In Solo mode a process that crashed leaves its envelopes owned by node 1, and the next process is node 1
// again. Startup released that ownership in the main store only, so an outgoing envelope stranded in an ANCILLARY
// store looked like the live node's own and was never sent (the reporter saw it only after another clean restart).
public class Bug_5733_solo_mode_releases_ancillary_store_ownership
{
    private const int UnreachablePort = 2_57_33;

    private static Task<IHost> startHostAsync(bool listen)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncPublisherHandlers))
                    .IncludeType(typeof(AncWorkHandlers));

                AncillaryStoreHarness.ConfigureStores(opts);

                opts.PublishMessage<AncWorkInA>().ToPort(UnreachablePort).UseDurableOutbox();
                if (listen) opts.ListenAtPort(UnreachablePort).UseDurableInbox();
            }).StartAsync();
    }

    [Fact]
    public async Task an_outgoing_envelope_stranded_in_an_ancillary_store_by_a_crash_is_sent_after_a_restart()
    {
        await AncillaryStoreHarness.EnsureDatabasesAsync();
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();

        using (var host = await startHostAsync(listen: false))
        {
            await host.ResetResourceState(token);

            // Nothing listens yet, so the outgoing row stays in store A's outbox
            await host.MessageBus().InvokeAsync(new AncKickOffFromA(id, StoreChoice.A), token);
            (await host.StrandedOutgoingAsync(typeof(AncWorkInA))).ShouldHaveSingleItem().ShouldStartWith("A:");

            await host.StopAsync(token);
        }

        // A graceful stop released the row. A crash would not have, so put it back the way the crash left it
        await using (var conn = new SqlConnection(AncillaryStoreHarness.ConnectionStringA))
        {
            await conn.OpenAsync(token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"update {AncillaryStoreHarness.WolverineSchema}.wolverine_outgoing_envelopes set owner_id = 1";
            (await cmd.ExecuteNonQueryAsync(token)).ShouldBe(1);
        }

        using (var host = await startHostAsync(listen: true))
        {
            await AncillaryStoreHarness.WaitForAsync(
                () => Task.FromResult(AncillaryStoreHarness.ExecutionsOf(id, "work") > 0), 20.Seconds());

            AncillaryStoreHarness.ExecutionsOf(id, "work")
                .ShouldBe(1, "the envelope stranded in the ancillary store was never recovered");

            await host.StopAsync(token);
        }
    }
}
