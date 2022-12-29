using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

public class DurableStorageSession : IDurableStorageSession
{
    private readonly CancellationToken _cancellation;
    private readonly DatabaseSettings _settings;

    public DurableStorageSession(DatabaseSettings settings, CancellationToken cancellation)
    {
        _settings = settings;
        _cancellation = cancellation;
    }

    public DbConnection? Connection { get; private set; }

    public DbTransaction? Transaction { get; private set; }

    public DbCommand CreateCommand(string sql)
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = Transaction;

        return cmd;
    }

    public DbCommand CallFunction(string functionName)
    {
        var cmd = CreateCommand(_settings.SchemaName + "." + functionName);
        cmd.CommandType = CommandType.StoredProcedure;

        return cmd;
    }

    public async Task WithinTransactionAsync(Func<Task> action)
    {
        await BeginAsync();

        try
        {
            await action();
        }
        catch (Exception)
        {
            await RollbackAsync();
            throw;
        }

        await CommitAsync();
    }

    public async Task WithinSessionGlobalLockAsync(int lockId, Func<Task> action)
    {
        var gotLock = await TryGetGlobalLockAsync(lockId);
        if (!gotLock)
        {
            return;
        }

        try
        {
            await action();
        }
        finally
        {
            await ReleaseGlobalLockAsync(lockId);
        }
    }

    public async Task WithinTransactionalGlobalLockAsync(int lockId, Func<Task> action)
    {
        await BeginAsync();

        var gotLock = await TryGetGlobalLockAsync(lockId);
        if (!gotLock)
        {
            await RollbackAsync();
            return;
        }

        try
        {
            await action();
        }
        catch (Exception)
        {
            await RollbackAsync();
            throw;
        }
        finally
        {
            await ReleaseGlobalLockAsync(lockId);
        }

        await CommitAsync();
    }


    public Task BeginAsync()
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        Transaction = Connection.BeginTransaction();
        return Task.CompletedTask;
    }

    public Task CommitAsync()
    {
        if (Transaction == null)
        {
            throw new InvalidOperationException("Transaction has not been started yet");
        }

        Transaction.Commit();
        Transaction = null;
        return Task.CompletedTask;
    }

    public Task RollbackAsync()
    {
        if (Transaction == null)
        {
            throw new InvalidOperationException("Transaction has not been started yet");
        }

        Transaction.Rollback();
        return Task.CompletedTask;
    }

    public Task ReleaseNodeLockAsync(int lockId)
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        return _settings.ReleaseGlobalLockAsync(Connection, lockId, _cancellation);
    }

    public Task GetNodeLockAsync(int lockId)
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        return _settings.GetGlobalLockAsync(Connection, lockId, _cancellation);
    }

    public Task<bool> TryGetGlobalTxLockAsync(int lockId)
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        if (Transaction == null)
        {
            throw new InvalidOperationException("Transaction has not been started yet");
        }

        return _settings.TryGetGlobalTxLockAsync(Connection, Transaction, lockId, _cancellation);
    }

    public Task<bool> TryGetGlobalLockAsync(int lockId)
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        return _settings.TryGetGlobalLockAsync(Connection, Transaction, lockId, _cancellation);
    }

    public Task ReleaseGlobalLockAsync(int lockId)
    {
        if (Connection == null)
        {
            throw new InvalidOperationException("Session has not been started yet");
        }

        return _settings.ReleaseGlobalLockAsync(Connection, lockId, _cancellation, Transaction);
    }

    public bool IsConnected()
    {
        return Connection?.State == ConnectionState.Open;
    }

    public async Task ConnectAndLockCurrentNodeAsync(ILogger logger, int nodeId)
    {
        if (Connection != null)
        {
            try
            {
                await Connection.CloseAsync();
                Connection.Dispose();
                Connection = null;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error while trying to close current connection");
            }
        }

        try
        {
            Connection = _settings.CreateConnection();

            await Connection.OpenAsync(_cancellation);

            await _settings.GetGlobalLockAsync(Connection, nodeId, _cancellation, Transaction);
        }
        catch (Exception)
        {
            Connection?.Dispose();
            Connection = null;

            throw;
        }
    }

    public void Dispose()
    {
        Connection?.Close();
        Transaction?.Dispose();
        Connection?.Dispose();
    }
}