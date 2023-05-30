using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

[Obsolete]
public interface IDurableStorageSession : IDisposable
{
    DbTransaction? Transaction { get; }
    CancellationToken Cancellation { get; }
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
    Task WithinTransactionAsync(Func<Task> action);
    Task WithinTransactionalGlobalLockAsync(int lockId, Func<Task> action);
    Task WithinSessionGlobalLockAsync(int lockId, Func<Task> action);
}