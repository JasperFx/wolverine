using System.Collections.Concurrent;
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
/// GH-5410. A handler's EF Core transaction can COMMIT on the server and still fail on the client, as a command
/// timeout or a dropped connection does while the reply is on its way. Wolverine treated that as a failure: the error
/// policy dead-lettered the message, which deleted the inbox row the commit had just marked Handled, so a replay ran
/// the committed work again. And the messages the handler cascaded to durable local queues were committed as inbox
/// rows owned by this live node but never handed to their queue, so they sat Incoming until the process died.
/// </summary>
public class Bug_5410_a_commit_that_applied_but_reported_an_error : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        CommittedThenThrowsInterceptor.ThrowAfterNextCommit = false;
        AmbiguousCommitHandler.Executions.Clear();
        AmbiguousCommitHandler.Cascaded.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString)
                        .AddInterceptors(new CommittedThenThrowsInterceptor()));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "gh5410");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                // Received on a durable endpoint, so the envelope is already in the inbox when the handler runs
                opts.Policies.UseDurableLocalQueues();

                // What a commit failure would otherwise lead to
                opts.OnException<AmbiguousCommitException>().MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AmbiguousCommitHandler));

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    [Fact]
    public async Task committed_work_is_not_dead_lettered_and_its_cascades_run()
    {
        using var host = await startAsync();

        var command = new AmbiguousCommit(Guid.NewGuid());
        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .WaitForExecutionOf<AmbiguousCommitCascade>()
            .SendMessageAndWaitAsync(command);

        // The handler ran once, and its cascade was handed to its queue rather than stranded in the inbox
        AmbiguousCommitHandler.Executions.Count(x => x == command.Id).ShouldBe(1);
        AmbiguousCommitHandler.Cascaded.ShouldContain(command.Id);

        var store = host.GetRuntime().Storage;
        (await store.Admin.FetchCountsAsync()).DeadLetter.ShouldBe(0);

        // Nothing is left Incoming. The receiver marks the cascade's row off to the side, so give it a moment
        var deadline = DateTimeOffset.UtcNow.Add(5.Seconds());
        IReadOnlyList<Envelope> rows;
        while (true)
        {
            rows = await store.Admin.AllIncomingAsync();
            if (rows.All(x => x.Status == EnvelopeStatus.Handled) || DateTimeOffset.UtcNow > deadline) break;
            await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);
        }

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(x => x.Status == EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task a_handled_envelope_is_never_moved_to_dead_letter_storage()
    {
        using var host = await startAsync();
        var store = host.GetRuntime().Storage;

        var envelope = new Envelope(new AmbiguousCommit(Guid.NewGuid()))
        {
            Id = Guid.NewGuid(),
            Destination = new Uri("local://gh5410"),
            MessageType = "gh5410",
            ContentType = EnvelopeConstants.JsonContentType,
            Data = [1, 2, 3]
        };

        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new AmbiguousCommitException());

        (await store.Admin.FetchCountsAsync()).DeadLetter.ShouldBe(0);
        (await store.Admin.AllIncomingAsync()).Single(x => x.Id == envelope.Id).Status
            .ShouldBe(EnvelopeStatus.Handled);
    }
}

public record AmbiguousCommit(Guid Id);

public record AmbiguousCommitCascade(Guid Id);

public class AmbiguousCommitException() : Exception("The commit applied, but its reply was lost");

/// <summary>
/// Lets the COMMIT reach the server, then fails as if its reply had been lost
/// </summary>
public class CommittedThenThrowsInterceptor : DbTransactionInterceptor
{
    public static volatile bool ThrowAfterNextCommit;

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (ThrowAfterNextCommit)
        {
            ThrowAfterNextCommit = false;
            throw new AmbiguousCommitException();
        }

        return Task.CompletedTask;
    }
}

public static class AmbiguousCommitHandler
{
    public static readonly ConcurrentBag<Guid> Executions = new();
    public static readonly ConcurrentBag<Guid> Cascaded = new();

    public static AmbiguousCommitCascade Handle(AmbiguousCommit message, CleanDbContext dbContext)
    {
        Executions.Add(message.Id);
        dbContext.Items.Add(new Item { Id = message.Id, Name = "committed once" });
        CommittedThenThrowsInterceptor.ThrowAfterNextCommit = true;

        return new AmbiguousCommitCascade(message.Id);
    }

    public static void Handle(AmbiguousCommitCascade message)
    {
        Cascaded.Add(message.Id);
    }
}
