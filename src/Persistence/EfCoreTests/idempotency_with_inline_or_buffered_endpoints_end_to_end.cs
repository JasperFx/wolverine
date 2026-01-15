using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Resources;
using Marten;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

public class idempotency_with_inline_or_buffered_endpoints_end_to_end : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await buildSqlServer();
    }

    private static async Task buildSqlServer()
    {
        var itemsTable = new Table(new DbObjectName("dbo", "items"));
        itemsTable.AddColumn<Guid>("Id").AsPrimaryKey();
        itemsTable.AddColumn<string>("Name");
        itemsTable.AddColumn<bool>("Approved");

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        var migration = await SchemaMigration.DetermineAsync(conn, itemsTable);
        if (migration.Difference != SchemaPatchDifference.None)
        {
            var sqlServerMigrator = new SqlServerMigrator();

            await sqlServerMigrator.ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
        }

        await conn.CloseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(IdempotencyStyle.Optimistic, true)]
    [InlineData(IdempotencyStyle.Eager, true)]
    [InlineData(IdempotencyStyle.Optimistic, false)]
    [InlineData(IdempotencyStyle.Eager, false)]
    public async Task happy_and_sad_path(IdempotencyStyle idempotency, bool isWolverineEnabled)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (isWolverineEnabled)
                {
                    opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }
                else
                {
                    opts.Services.AddDbContext<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }
                
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                
                opts.Policies.AutoApplyTransactions(idempotency);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotency");
                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync();

        var messageId = Guid.NewGuid();
        var tracked1 = await host.SendMessageAndWaitAsync(new MaybeIdempotent(messageId));

        // First time through should be perfectly fine
        var sentMessage = tracked1.Executed.SingleEnvelope<MaybeIdempotent>();

        var runtime = host.GetRuntime();
        var circuit = runtime.Endpoints.FindListenerCircuit(sentMessage.Destination);

        var tracked2 = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
            {
                sentMessage.WasPersistedInInbox = false;
                sentMessage.Attempts = 0;
                return circuit.EnqueueDirectlyAsync([sentMessage]);
            });

        tracked2.Discarded.SingleEnvelope<MaybeIdempotent>().ShouldNotBeNull();
    }
    
    [Theory]
    [InlineData(IdempotencyStyle.Optimistic, true)]
    [InlineData(IdempotencyStyle.Eager, true)]
    [InlineData(IdempotencyStyle.Optimistic, false)]
    [InlineData(IdempotencyStyle.Eager, false)]
    public async Task happy_and_sad_path_with_message_and_destination_tracking(IdempotencyStyle idempotency, bool isWolverineEnabled)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (isWolverineEnabled)
                {
                    opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }
                else
                {
                    opts.Services.AddDbContext<CleanDbContext>(x =>
                        x.UseSqlServer(Servers.SqlServerConnectionString));
                }
                
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                
                opts.Policies.AutoApplyTransactions(idempotency);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotency");
                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync();

        var messageId = Guid.NewGuid();
        var tracked1 = await host.SendMessageAndWaitAsync(new MaybeIdempotent(messageId));

        // First time through should be perfectly fine
        var sentMessage = tracked1.Executed.SingleEnvelope<MaybeIdempotent>();

        var runtime = host.GetRuntime();
        var circuit = runtime.Endpoints.FindListenerCircuit(sentMessage.Destination);

        var tracked2 = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
            {
                sentMessage.WasPersistedInInbox = false;
                sentMessage.Attempts = 0;
                return circuit.EnqueueDirectlyAsync([sentMessage]);
            });

        tracked2.Discarded.SingleEnvelope<MaybeIdempotent>().ShouldNotBeNull();
    }

    [Fact]
    public async Task apply_idempotency_to_non_transactional_handler()
    {
        #region sample_using_AutoApplyIdempotencyOnNonTransactionalHandlers

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                
                opts.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotency");
                opts.UseEntityFrameworkCoreTransactions();
                
                // THIS RIGHT HERE
                opts.Policies.AutoApplyIdempotencyOnNonTransactionalHandlers();
            }).StartAsync();

        #endregion

        var chain = host.GetRuntime().Handlers.ChainFor<MaybeIdempotentNotTransactional>();
        chain.IsTransactional.ShouldBeFalse();
        chain.Middleware.OfType<MethodCall>().Any(x => x.Method.Name == nameof(MessageContext.AssertEagerIdempotencyAsync)).ShouldBeTrue();
        chain.Postprocessors.OfType<MethodCall>().Any(x => x.Method.Name == nameof(MessageContext.PersistHandledAsync)).ShouldBeTrue();
        
        var messageId = Guid.NewGuid();
        var tracked1 = await host.SendMessageAndWaitAsync(new MaybeIdempotentNotTransactional(messageId));

        // First time through should be perfectly fine
        var sentMessage = tracked1.Executed.SingleEnvelope<MaybeIdempotentNotTransactional>();

        var runtime = host.GetRuntime();
        var circuit = runtime.Endpoints.FindListenerCircuit(sentMessage.Destination);

        var tracked2 = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
            {
                sentMessage.WasPersistedInInbox = false;
                sentMessage.Attempts = 0;
                return circuit.EnqueueDirectlyAsync([sentMessage]);
            });

        tracked2.Discarded.SingleEnvelope<MaybeIdempotentNotTransactional>().ShouldNotBeNull();
    }
}

public record MaybeIdempotent(Guid Id);
public record MaybeIdempotentNotTransactional(Guid Id);

public static class MaybeIdempotentHandler
{
    // public static Insert<Item> Handle(MaybeIdempotent message)
    // {
    //     return Storage.Insert(new Item { Id = message.Id });
    // }
    
    public static void Handle(MaybeIdempotent message, CleanDbContext dbContext)
    {
        // Nothing
    }

    public static void Handle(MaybeIdempotentNotTransactional message)
    {
        // Nothing
    }
}
