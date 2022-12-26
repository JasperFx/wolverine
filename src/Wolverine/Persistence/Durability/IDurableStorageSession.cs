using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

public interface IDurableStorageSession : IDisposable
{
    DbTransaction? Transaction { get; }
    Task ReleaseNodeLockAsync(int lockId);
    Task GetNodeLockAsync(int lockId);
    Task BeginAsync();
    Task CommitAsync();
    Task RollbackAsync();

    Task<bool> TryGetGlobalTxLockAsync(int lockId);
    Task<bool> TryGetGlobalLockAsync(int lockId);
    Task ReleaseGlobalLockAsync(int lockId);

    bool IsConnected();
    Task ConnectAndLockCurrentNodeAsync(ILogger logger, int nodeId);
    DbCommand CallFunction(string functionName);
    DbCommand CreateCommand(string sql);
}