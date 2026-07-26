using System.Data.Common;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Exceptions;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.Polling;

public class DatabaseBatchCommandException : Exception
{
    public DatabaseBatchCommandException(DbCommand command, IDatabaseOperation[] operations, Exception inner) : base(toMessage(command, operations), inner)
    {
    }

    private static string toMessage(DbCommand command, IDatabaseOperation[] operations)
    {
        var message = "Database operation batch failure:\n";

        var count = 0;
        foreach (var operation in operations)
        {
            message += $"{++count}. {operation}\n";
        }

        message += command.CommandText;
        foreach (DbParameter parameter in command.Parameters)
            message += $"\n{parameter.ParameterName}: {parameter.Value}";

        return message;
    }
}

internal class DatabaseOperationBatch : IAgentCommand
{
    private readonly IMessageDatabase _database;
    private readonly IDatabaseOperation[] _operations;

    public DatabaseOperationBatch(IMessageDatabase database, IDatabaseOperation[] operations)
    {
        _database = database;
        _operations = operations;
    }

    public async Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (_operations.Length == 0) return AgentCommands.Empty;

        var builder = _database.ToCommandBuilder();

        // Mark a statement boundary before each operation and remember which command each one's
        // results will come back on. On every provider whose driver can execute several statements
        // from a single command -- which is all of them except Oracle -- StartNewCommand() is a
        // no-op, CompileCommands() returns one command holding every statement, and all of this
        // collapses to exactly what it has always done. Oracle's builder splits at each boundary.
        //
        // An operation may contribute more than one statement (ReleaseOrphanedMessagesOperation,
        // MoveReplayableErrorMessagesToIncomingOperation, PersistNodeRecord) or none at all
        // (ReleaseOrphanedMessagesForAncillaryOperation, when it has no active node numbers). Every
        // such operation is IDoNotReturnData -- each of the four that *do* return data appends
        // exactly one statement unconditionally -- so associating an operation with the first command
        // it produced is enough. Any further commands execute with no callbacks, and a zero-statement
        // operation clamps into the last group where ApplyCallbacksAsync skips it as IDoNotReturnData.
        var starts = new int[_operations.Length];
        for (var i = 0; i < _operations.Length; i++)
        {
            builder.StartNewCommand();

            // Nothing is open at this point, so the builder's count is the index the next command
            // it produces will occupy
            starts[i] = builder.CommandCount;
            _operations[i].ConfigureCommand(builder);
        }

        var commands = builder.CompileCommands();
        if (commands.Count == 0) return AgentCommands.Empty;

        var groups = new List<IDatabaseOperation>[commands.Count];
        for (var i = 0; i < commands.Count; i++) groups[i] = [];
        for (var i = 0; i < _operations.Length; i++)
        {
            groups[Math.Min(starts[i], commands.Count - 1)].Add(_operations[i]);
        }

        await using var conn = await _database.DataSource.OpenConnectionAsync(cancellationToken);

        var tx = await conn.BeginTransactionAsync(cancellationToken);

        DbCommand? current = null;
        try
        {
            var exceptions = new List<Exception>();

            for (var i = 0; i < commands.Count; i++)
            {
                current = commands[i];
                current.Connection = conn;
                current.Transaction = tx;

                await using var reader = await current.ExecuteReaderAsync(cancellationToken);
                if (groups[i].Count != 0)
                {
                    await ApplyCallbacksAsync(groups[i], reader, exceptions, cancellationToken);
                }

                await reader.CloseAsync();
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The system is shutting down, let this go.
        }
        catch (Exception e)
        {
            await conn.CloseAsync();
            throw new DatabaseBatchCommandException(current!, _operations, e);
        }
        finally
        {
            foreach (var command in commands)
            {
                await command.DisposeAsync();
            }
        }

        try
        {
            return postProcessingCommands();
        }
        finally
        {
            try
            {
                await conn.CloseAsync();
            }
            catch (Exception )
            {
                // Don't let an exception get out of there. 
            }
        }
    }

    private AgentCommands postProcessingCommands()
    {
        var commands = new AgentCommands();
        foreach (var operation in _operations)
        {
            commands.AddRange(operation.PostProcessingCommands());
        }

        return commands;
    }

    public static async Task ApplyCallbacksAsync(IReadOnlyList<IDatabaseOperation> operations, DbDataReader reader,
        IList<Exception> exceptions,
        CancellationToken token)
    {
        var first = operations[0];

        if (!(first is IDoNotReturnData))
        {
            await first.ReadResultsAsync(reader, exceptions, token).ConfigureAwait(false);
            try
            {
                await reader.NextResultAsync(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                if (first is IExceptionTransform t && t.TryTransform(e, out var transformed))
                {
                    throw transformed;
                }

                throw;
            }
        }

        for (var i = 1; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation is IDoNotReturnData)
            {
                continue;
            }

            await operation.ReadResultsAsync(reader, exceptions, token).ConfigureAwait(false);
            try
            {
                await reader.NextResultAsync(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                if (operation is IExceptionTransform t && t.TryTransform(e, out var transformed))
                {
                    throw transformed;
                }

                throw;
            }
        }
    }
}