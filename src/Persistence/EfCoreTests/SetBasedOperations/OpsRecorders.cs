using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCoreTests.SetBasedOperations;

/// <summary>
///     Records the SQL an EF Core DbContext actually sent, so a test can assert on the number of
///     round trips rather than on the state they left behind. Process-global on purpose -- this
///     assembly disables test parallelization (see NoParallelization.cs), and an interceptor is
///     registered once with the DbContext rather than resolved per test.
/// </summary>
public class OpsCommandRecorder : DbCommandInterceptor
{
    public static readonly OpsCommandRecorder Instance = new();

    private static readonly List<string> _commands = [];
    private static bool _recording;

    public static void Start()
    {
        lock (_commands)
        {
            _commands.Clear();
            _recording = true;
        }
    }

    public static string[] Stop()
    {
        lock (_commands)
        {
            _recording = false;
            return _commands.ToArray();
        }
    }

    private static void record(DbCommand command)
    {
        lock (_commands)
        {
            if (_recording)
            {
                _commands.Add(command.CommandText);
            }
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        record(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        record(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    {
        record(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        record(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result)
    {
        record(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        record(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
///     Counts <c>SaveChangesAsync</c> calls, so a test can prove a bulk insert went through exactly
///     one of them.
/// </summary>
public class OpsSaveChangesRecorder : SaveChangesInterceptor
{
    public static readonly OpsSaveChangesRecorder Instance = new();

    private static int _count;
    private static bool _recording;

    public static void Start()
    {
        _count = 0;
        _recording = true;
    }

    public static int Stop()
    {
        _recording = false;
        return _count;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (_recording) Interlocked.Increment(ref _count);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_recording) Interlocked.Increment(ref _count);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
