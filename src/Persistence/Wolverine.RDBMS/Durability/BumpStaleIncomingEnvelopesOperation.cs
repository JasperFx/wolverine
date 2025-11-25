using System.Data.Common;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

internal class BumpStaleIncomingEnvelopesOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly DbObjectName _incomingTable;
    private readonly DurabilitySettings _durability;
    private readonly DateTimeOffset _timestamp;

    public BumpStaleIncomingEnvelopesOperation(DbObjectName incomingTable, DurabilitySettings durability, DateTimeOffset utcNow)
    {
        _incomingTable = incomingTable;
        _durability = durability;
        _timestamp = utcNow.Subtract(_durability.InboxStaleTime.Value);
    }

    public string Description => "Bump stale or stuck inbox messages to be picked up by other nodes";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        builder.Append(
            $"update {_incomingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.Timestamp} <= ");
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