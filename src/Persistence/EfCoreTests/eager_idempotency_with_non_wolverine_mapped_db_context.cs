using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace EfCoreTests;

public class eager_idempotency_with_non_wolverine_mapped_db_context : IClassFixture<EFCorePersistenceContext>
{
    public eager_idempotency_with_non_wolverine_mapped_db_context(EFCorePersistenceContext context)
    {
        Host = context.theHost;
    }

    public IHost Host { get; }
    
        [Fact]
    public async Task happy_path_eager_idempotency()
    {
        await Host.RebuildAllEnvelopeStorageAsync();
        
        var runtime = Host.GetRuntime();
        var envelope = ObjectMother.Envelope();

        var context = new MessageContext(runtime);
        context.ReadEnvelope(envelope, Substitute.For<IChannelCallback>());

        using var scope = Host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
        
        var transaction = new EfCoreEnvelopeTransaction(dbContext, context);

        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, new DurabilitySettings(), CancellationToken.None);
        ok.ShouldBeTrue();

        await dbContext.Database.CurrentTransaction!.CommitAsync();

        var persisted = (await runtime.Storage.Admin.AllIncomingAsync()).Single(x => x.Id == envelope.Id);
        persisted.Data.Length.ShouldBe(0);
        persisted.Destination.ShouldBe(envelope.Destination);
        persisted.MessageType.ShouldBe(envelope.MessageType);
        persisted.Status.ShouldBe(EnvelopeStatus.Handled);
        persisted.KeepUntil.HasValue.ShouldBeTrue();
        
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        
        var raw = await conn
            .CreateCommand($"select keep_until from dbo.{DatabaseConstants.IncomingTable} where id = @id")
            .With("id", persisted.Id)
            .ExecuteScalarAsync();

        raw.ShouldNotBeNull();
        raw.ShouldBeOfType<DateTimeOffset>().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        
    }
    
    [Fact]
    public async Task sad_path_eager_idempotency()
    {
        var runtime = Host.GetRuntime();
        var envelope = ObjectMother.Envelope();

        var context = new MessageContext(runtime);
        context.ReadEnvelope(envelope, Substitute.For<IChannelCallback>());

        using var scope = Host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();
        
        var transaction = new EfCoreEnvelopeTransaction(dbContext, context);

        var durabilitySettings = new DurabilitySettings();
        var ok = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, durabilitySettings, CancellationToken.None);
        ok.ShouldBeTrue();
        await dbContext.Database.CurrentTransaction!.CommitAsync();
        
        // Kind of resetting it here
        envelope.WasPersistedInInbox = false;
        
        var secondTime = await transaction.TryMakeEagerIdempotencyCheckAsync(envelope, durabilitySettings, CancellationToken.None);
        secondTime.ShouldBeFalse();

        
    }
}