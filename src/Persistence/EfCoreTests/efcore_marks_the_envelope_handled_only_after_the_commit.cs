using System.Data.Common;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

/// <summary>
/// F25 (durable compliance rig). On a durable endpoint, <c>EfCoreEnvelopeTransaction.CommitAsync</c> marks the inbox
/// row Handled inside the handler's transaction and set <c>Envelope.Status = Handled</c> BEFORE committing. When the
/// commit failed and the error policy discarded the message, the durable receiver saw Handled and skipped its own
/// mark, so the row stayed Incoming and the message ran again after a restart. Marten and Polecat only flip the
/// status once the commit has succeeded.
/// </summary>
public class efcore_marks_the_envelope_handled_only_after_the_commit : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        FailingCommitInterceptor.FailNextCommit = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task a_discarded_message_whose_commit_failed_is_still_settled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString).AddInterceptors(new FailingCommitInterceptor()));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "handled_after_commit");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.OnException(e => e is CommitFailure || e.InnerException is CommitFailure).Discard();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CommitFailsHandler));

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new CommitFails(Guid.NewGuid()));

        var envelope = tracked.Discarded.SingleEnvelope<CommitFails>();

        // The receiver settles the row off to the side, so give it a moment
        var store = host.GetRuntime().Storage;
        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        Envelope? row = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            row = (await store.Admin.AllIncomingAsync()).SingleOrDefault(x => x.Id == envelope.Id);
            if (row?.Status == EnvelopeStatus.Handled) break;
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        row.ShouldNotBeNull();
        row.Status.ShouldBe(EnvelopeStatus.Handled);
    }
}

public record CommitFails(Guid Id);

public class CommitFailure : Exception;

public class FailingCommitInterceptor : DbTransactionInterceptor
{
    public static volatile bool FailNextCommit;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
        TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (FailNextCommit)
        {
            FailNextCommit = false;
            throw new CommitFailure();
        }

        return ValueTask.FromResult(result);
    }
}

public static class CommitFailsHandler
{
    public static void Handle(CommitFails message, CleanDbContext dbContext)
    {
        dbContext.Items.Add(new Item { Id = message.Id, Name = "never committed" });
        FailingCommitInterceptor.FailNextCommit = true;
    }
}
