using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using NSubstitute;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace EfCoreTests;

[Collection("sqlserver")]
public class using_add_dbcontext_with_wolverine_integration : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "idempotency");
                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync();

        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
    
    public Table ItemsTable { get; }

    [Fact]
    public void is_wolverine_enabled()
    {
        using var nested = _host.Services.CreateScope();
        nested.ServiceProvider.GetRequiredService<CleanDbContext>().IsWolverineEnabled().ShouldBeTrue();
    }
    
    [Fact]
    public async Task happy_path_eager_idempotency()
    {
        var runtime = _host.GetRuntime();
        var envelope = ObjectMother.Envelope();

        var context = new MessageContext(runtime);
        context.ReadEnvelope(envelope, Substitute.For<IChannelCallback>());

        using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
        
        var transaction = new EfCoreEnvelopeTransaction(dbContext, context);

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, CancellationToken.None);
        ok.ShouldBeTrue();

        await dbContext.Database.CurrentTransaction!.CommitAsync();

        var all = await runtime.Storage.Admin.AllIncomingAsync();
        
        var persisted = (await runtime.Storage.Admin.AllIncomingAsync()).Single(x => x.Id == envelope.Id);
        persisted.Data.Length.ShouldBe(0);
        persisted.Destination.ShouldBe(envelope.Destination);
        persisted.MessageType.ShouldBe(envelope.MessageType);
        persisted.Status.ShouldBe(EnvelopeStatus.Handled);
        
    }
    
    [Fact]
    public async Task sad_path_eager_idempotency()
    {
        var runtime = _host.GetRuntime();
        var envelope = ObjectMother.Envelope();

        var context = new MessageContext(runtime);
        context.ReadEnvelope(envelope, Substitute.For<IChannelCallback>());

        using var scope = _host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CleanDbContext>();
        
        var transaction = new EfCoreEnvelopeTransaction(dbContext, context);

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, CancellationToken.None);
        ok.ShouldBeTrue();
        await dbContext.Database.CurrentTransaction!.CommitAsync();
        
        // Kind of resetting it here
        envelope.WasPersistedInInbox = false;
        
        var secondTime = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, CancellationToken.None);
        secondTime.ShouldBeFalse();

        
    }
    
    
    private async Task withItemsTable()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            var migration = await SchemaMigration.DetermineAsync(conn, ItemsTable);
            if (migration.Difference != SchemaPatchDifference.None)
            {
                var sqlServerMigrator = new SqlServerMigrator();
                
                await sqlServerMigrator.ApplyAllAsync(conn, migration, AutoCreate.CreateOrUpdate);
            }

            await conn.CloseAsync();
        }
    }
}

