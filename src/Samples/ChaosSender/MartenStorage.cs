using IntegrationTests;
using JasperFx.Core;
using Lamar;
using Marten;
using Npgsql;
using Oakton.Resources;
using Weasel.Core;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

namespace ChaosSender;

public interface IMessageStorageStrategy
{
    void ConfigureReceiverPersistence(WolverineOptions options);
    void ConfigureSenderPersistence(WolverineOptions options);
    Task ClearMessageRecords(IContainer services);
    Task<long> FindOutstandingMessageCount(IContainer container, CancellationToken cancellation);
}

public class MartenStorageStrategy : IMessageStorageStrategy
{
    public override string ToString()
    {
        return "Marten Persistence";
    }

    public void ConfigureReceiverPersistence(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "chaos";

            m.RegisterDocumentType<MessageRecord>();

            m.AutoCreateSchemaObjects = AutoCreate.None;
        }).IntegrateWithWolverine("chaos_receiver");

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }

    public void ConfigureSenderPersistence(WolverineOptions opts)
    {
        opts.Policies.OnException<PostgresException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "chaos";

            m.RegisterDocumentType<MessageRecord>();

            m.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
        }).IntegrateWithWolverine("chaos_sender");

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }

    public Task ClearMessageRecords(IContainer services)
    {
        var store = services.GetInstance<IDocumentStore>();
        return store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public async Task<long> FindOutstandingMessageCount(IContainer container, CancellationToken cancellation)
    {
        var store = container.GetInstance<IDocumentStore>();
        await using var session = store.LightweightSession();

        return await session.Query<MessageRecord>().CountAsync(cancellation);
    }
}

public class MartenMessageRecordRepository : IMessageRecordRepository
{
    private readonly IDocumentSession _session;

    public MartenMessageRecordRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<long> FindOutstandingMessageCount(CancellationToken token)
    {
        var count = await _session.Query<MessageRecord>().CountAsync(token);

        return count;
    }

    public void MarkNew(MessageRecord record)
    {
        _session.Store(record);
    }

    public ValueTask MarkDeleted(Guid id)
    {
        _session.Delete<MessageRecord>(id);
        return new ValueTask();
    }

    public Task ClearMessageRecords()
    {
        return _session.DocumentStore.Advanced.Clean.DeleteAllDocumentsAsync();
    }
}
