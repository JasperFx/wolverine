namespace ChaosSender;

public interface IMessageRecordRepository
{
    Task<long> FindOutstandingMessageCount(CancellationToken token);

    void MarkNew(MessageRecord record);
    ValueTask MarkDeleted(Guid id);

    Task ClearMessageRecords();
}