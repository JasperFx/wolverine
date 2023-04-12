using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;

namespace ChaosTesting;

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
        }).IntegrateWithWolverine("chaos_receiver");
        
        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }

    public void ConfigureSenderPersistence(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "chaos";
        }).IntegrateWithWolverine("chaos_sender");
                
        opts.Policies.AutoApplyTransactions();
        
        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }
}

public class MartenMessageRecordRepository : IMessageRecordRepository
{
    private readonly IDocumentSession _session;

    public MartenMessageRecordRepository(IDocumentSession session)
    {
        _session = session;
    }

    public Task<long> FindOutstandingMessageCount(CancellationToken token)
    {
        return _session.Query<MessageRecord>().CountLongAsync(token);
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