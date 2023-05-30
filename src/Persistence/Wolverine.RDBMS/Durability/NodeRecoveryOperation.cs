using System.Runtime.CompilerServices;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Durability;

internal class NodeRecoveryOperation : IAgentCommand
{
    private readonly int _ownerNodeId;

    public NodeRecoveryOperation(int ownerNodeId)
    {
        _ownerNodeId = ownerNodeId;
    }

    public async IAsyncEnumerable<object> ExecuteAsync(IWolverineRuntime runtime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await runtime.Storage.Admin.ReleaseAllOwnershipAsync();

        yield break;
    }
}