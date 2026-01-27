using System.Data.Common;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class BumpStaleOutgoingEnvelopesOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly DbObjectName _outgoingTable;
    private readonly DurabilitySettings _durability;
    private readonly DateTimeOffset _timestamp;

    public BumpStaleOutgoingEnvelopesOperation(DbObjectName outgoingTable, DurabilitySettings durability, DateTimeOffset utcNow)
    {
        _outgoingTable = outgoingTable;
        _durability = durability;
        _timestamp = utcNow.Subtract(_durability.OutboxStaleTime.Value);
    }

    public string Description => "Bump stale or stuck outbox messages to be picked up by other nodes";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append(
            $"update {_outgoingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.Timestamp} <= ");
        builder.AppendParameter(_timestamp);
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