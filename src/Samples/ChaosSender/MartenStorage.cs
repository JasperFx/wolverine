using Marten;

namespace ChaosSender;

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