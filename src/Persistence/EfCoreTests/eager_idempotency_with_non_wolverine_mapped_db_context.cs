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
using Wolverine.EntityFrameworkCore;
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
        persisted.Data!.Length.ShouldBe(0);
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
    
    // Regression test for https://github.com/JasperFx/wolverine/issues/2474
    [Fact]
    public async Task persist_batch_outgoing_envelopes_uses_outgoing_table()
    {
        await Host.RebuildAllEnvelopeStorageAsync();

        var runtime = Host.GetRuntime();
        var context = new MessageContext(runtime);

        using var scope = Host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ItemsDbContext>();

        dbContext.IsWolverineEnabled().ShouldBeFalse();

        var envelope1 = new Envelope
        {
            Id = Guid.NewGuid(),
            Data = [1, 2, 3, 4],
            MessageType = "Something",
            Destination = new Uri("tcp://localhost:2222"),
            ContentType = EnvelopeConstants.JsonContentType,
            OwnerId = 567,
            Attempts = 1,
            DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(28))
        };
        var envelope2 = new Envelope
        {
            Id = Guid.NewGuid(),
            Data = [5, 6, 7, 8],
            MessageType = "SomethingElse",
            Destination = new Uri("tcp://localhost:2222"),
            ContentType = EnvelopeConstants.JsonContentType,
            OwnerId = 567,
            Attempts = 1,
            DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(28))
        };

        var transaction = new EfCoreEnvelopeTransaction(dbContext, context);
        await transaction.PersistOutgoingAsync([envelope1, envelope2]);
        await dbContext.Database.CurrentTransaction!.CommitAsync();

        var outgoing = await runtime.Storage.Admin.AllOutgoingAsync();
        outgoing.ShouldContain(x => x.Id == envelope1.Id);
        outgoing.ShouldContain(x => x.Id == envelope2.Id);

        var incoming = await runtime.Storage.Admin.AllIncomingAsync();
        incoming.ShouldNotContain(x => x.Id == envelope1.Id);
        incoming.ShouldNotContain(x => x.Id == envelope2.Id);
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