using System.Data.Common;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class DeleteExpiredEnvelopesOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly DbObjectName _incomingTable;
    private readonly DateTimeOffset _utcNow;

    public DeleteExpiredEnvelopesOperation(DbObjectName incomingTable, DateTimeOffset utcNow)
    {
        _incomingTable = incomingTable;
        _utcNow = utcNow;
    }

    public string Description => "Delete expired incoming envelopes";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append(
            $"delete from {_incomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Handled}' and {DatabaseConstants.KeepUntil} <= ");
        builder.AppendParameter(_utcNow);
        builder.Append(';');
    }

    public Task ReadResultsAsync(DbDataReader reader, IList<Exception> exceptions, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public IEnumerable<IAgentCommand> PostProcessingCommands()
    {
        yield break;
    }
}