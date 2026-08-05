using System.Data.Common;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime.Agents;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.RDBMS.Durability;

/// <summary>
/// Releases inbox/outbox messages owned by nodes that no longer exist.
/// Used for ancillary/tenant databases that lack the wolverine_nodes table,
/// so the active node list must be provided externally.
/// </summary>
internal class ReleaseOrphanedMessagesForAncillaryOperation : IDatabaseOperation, IDoNotReturnData
{
    private readonly IMessageDatabase _database;
    private readonly IReadOnlyList<int> _activeNodeNumbers;
    private readonly int _highWaterMark;

    /// <param name="highWaterMark">
    /// The highest node number the active list has ever been known to cover. Owners above it
    /// registered after that list was taken, so it says nothing about them and they are left alone
    /// (GH-3850). Pass 0 to disable the guard, which restores the un-bounded behaviour.
    /// </param>
    public ReleaseOrphanedMessagesForAncillaryOperation(IMessageDatabase database,
        IReadOnlyList<int> activeNodeNumbers, int highWaterMark = 0)
    {
        _database = database;
        _activeNodeNumbers = activeNodeNumbers;
        _highWaterMark = highWaterMark;
    }

    public string Description => "Release inbox/outbox messages owned by nodes that no longer exist (ancillary database)";

    public void ConfigureCommand(DbCommandBuilder builder)
    {
        if (_activeNodeNumbers.Count == 0) return;

        var schemaName = _database.SchemaName;
        var incomingTable = new DbObjectName(schemaName, DatabaseConstants.IncomingTable);
        var outgoingTable = new DbObjectName(schemaName, DatabaseConstants.OutgoingTable);
        var nodeList = string.Join(", ", _activeNodeNumbers);

        // GH-3850. The list is cached per node for up to one polling interval, so it cannot describe
        // a node that registered after it was taken -- and releasing a LIVE node's rows hands its
        // in-flight work to somebody else. Node numbers are monotonic, so anything above the mark is
        // newer than the list and is not ours to judge.
        var ceiling = _highWaterMark > 0
            ? $" and {DatabaseConstants.OwnerId} <= {_highWaterMark}"
            : string.Empty;

        builder.Append(
            $"update {incomingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.OwnerId} not in ({nodeList}){ceiling};");

        // Two statements in one operation, so the boundary has to be explicit for the providers
        // that cannot execute several statements from one command
        builder.StartNewCommand();

        builder.Append(
            $"update {outgoingTable} set {DatabaseConstants.OwnerId} = 0 where {DatabaseConstants.OwnerId} != 0 and {DatabaseConstants.OwnerId} not in ({nodeList}){ceiling};");
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
