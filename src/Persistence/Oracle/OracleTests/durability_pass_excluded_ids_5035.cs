using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;
using Wolverine.Util;

namespace OracleTests;

/// <summary>
/// GH-5035. A durability pass step excludes the rows that already failed in the pass, one bind per id in an IN
/// list on Oracle. While some rows keep succeeding the step keeps going, so with enough failures the list would
/// pass Oracle's documented 1,000 expression ceiling for an IN list (ORA-01795). The step stops for the pass once
/// it has more than DurabilityPassContext.MaxExcludedIds failures instead. The assertions check that bound
/// directly, because not every Oracle version refuses a longer list.
/// </summary>
[Collection("oracle")]
public class durability_pass_excluded_ids_5035 : IAsyncLifetime
{
    private const string SchemaName = "DURABILITY_PASS_EXCLUDED";

    // More failing rows than Oracle takes in an IN list, one healthy row after every nine failing ones so
    // every batch makes progress
    private const int FailingRows = 1_100;
    private const int BatchSize = 100;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;
    private OracleMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ExcludedIdsMessageHandler));
                opts.OnException<ExcludedIdsException>().RequeueIndefinitely();
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, SchemaName);
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        theStore = theHost.GetRuntime().Storage.As<OracleMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static IEnumerable<bool> failingAndHealthy()
    {
        for (var i = 0; i < FailingRows; i++)
        {
            yield return true;
            if (i % 9 == 8) yield return false;
        }
    }

    private static async Task executeAsync(string sql)
    {
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<DurabilityPassReport> runPassAsync()
    {
        return await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 5.Minutes(),
            BatchSize = BatchSize
        }, Token);
    }

    private DurabilityPassStepReport stepReport(DurabilityPassReport report, string stepName)
    {
        return report.Stores.Single(x => x.StoreUri == theStore.Uri).Steps.Single(x => x.StepName == stepName);
    }

    [Fact]
    public async Task the_outbox_step_stops_before_its_excluded_ids_outgrow_the_in_list_limit()
    {
        // Oldest first, in the order written. The unknown scheme cannot be sent to
        foreach (var chunk in failingAndHealthy().Chunk(50))
        {
            var envelopes = chunk.Select(fails =>
            {
                var envelope = ObjectMother.Envelope();
                envelope.Destination = (fails ? "nowhere://down" : "stub://healthy").ToUri();
                envelope.DeliverBy = null;
                return envelope;
            }).ToArray();

            await using var conn = new OracleConnection(Servers.OracleConnectionString);
            await conn.OpenAsync(Token);
            await using var tx = await conn.BeginTransactionAsync(Token);
            await theStore.StoreOutgoingAsync(tx, envelopes);
            await tx.CommitAsync(Token);
        }

        // Stale, keeping the order they were written in
        await executeAsync(
            $"UPDATE {SchemaName}.{DatabaseConstants.OutgoingTable} SET \"TIMESTAMP\" = \"TIMESTAMP\" - NUMTODSINTERVAL(7200, 'SECOND')");

        var report = await runPassAsync();

        var step = stepReport(report, "Recover stale outgoing messages");
        step.Status.ShouldBe(DurabilityPassStepStatus.Completed);
        step.Result.OutgoingFailed.ShouldBeGreaterThan(DurabilityPassContext.MaxExcludedIds);
        step.Result.OutgoingFailed.ShouldBeLessThanOrEqualTo(DurabilityPassContext.MaxExcludedIds + BatchSize);
        step.Result.OutgoingSent.ShouldBeGreaterThan(0);
        report.HasMoreWork.ShouldBeTrue();

        // A later pass hits the same failures and stops the same way
        var second = await runPassAsync();
        stepReport(second, "Recover stale outgoing messages").Status.ShouldBe(DurabilityPassStepStatus.Completed);
    }

    [Fact]
    public async Task the_inbox_steps_stop_before_their_excluded_ids_outgrow_the_in_list_limit()
    {
        // Oldest first by execution time. A failing message is requeued, which leaves its row for a later pass
        var dueAt = DateTimeOffset.UtcNow.Subtract(1.Hours());
        var envelopes = failingAndHealthy().Select((fails, i) =>
        {
            var message = new ExcludedIdsMessage(fails);
            var envelope = new Envelope(message)
            {
                Destination = "stub://inbox".ToUri(),
                MessageType = typeof(ExcludedIdsMessage).ToMessageTypeName(),
                ContentType = EnvelopeConstants.JsonContentType,
                Status = EnvelopeStatus.Scheduled,
                ScheduledTime = dueAt.AddMilliseconds(i * 10),
                OwnerId = 0
            };

            envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);
            return envelope;
        }).ToArray();

        foreach (var chunk in envelopes.Chunk(50))
        {
            await theStore.Inbox.StoreIncomingAsync(chunk);
        }

        var report = await runPassAsync();

        var step = stepReport(report, "Release due scheduled messages");
        step.Status.ShouldBe(DurabilityPassStepStatus.Completed);
        step.Result.ScheduledReleased.ShouldBeGreaterThan(0);
        report.FailedSteps.ShouldBe(0);
        report.HasMoreWork.ShouldBeTrue();

        // A later pass hits the same failures and stops the same way
        var second = await runPassAsync();
        second.FailedSteps.ShouldBe(0);
    }
}

public record ExcludedIdsMessage(bool Fail);

public class ExcludedIdsException : Exception;

// Kept out of conventional discovery. The excluded ids host includes it explicitly
[WolverineIgnore]
public static class ExcludedIdsMessageHandler
{
    public static void Handle(ExcludedIdsMessage message)
    {
        if (message.Fail) throw new ExcludedIdsException();
    }
}
