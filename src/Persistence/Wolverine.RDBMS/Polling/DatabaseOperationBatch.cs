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
        foreach (var operation in _operations) operation.ConfigureCommand(builder);

        var cmd = builder.Compile();

        await using var conn = await _database.DataSource.OpenConnectionAsync(cancellationToken);

        cmd.Connection = conn;
        var tx = await conn.BeginTransactionAsync(cancellationToken);
        cmd.Transaction = tx;

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var exceptions = new List<Exception>();
            await ApplyCallbacksAsync(_operations, reader, exceptions, cancellationToken);
            await reader.CloseAsync();

            await tx.CommitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The system is shutting down, let this go. 
        }
        catch (Exception e)
        {
            await conn.CloseAsync();
            throw new DatabaseBatchCommandException(cmd, _operations, e);
        }

        try
        {
            var commands = new AgentCommands();
            commands.AddRange(_operations.SelectMany(x => x.PostProcessingCommands()));
            return commands;
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

    public static async Task ApplyCallbacksAsync(IReadOnlyList<IDatabaseOperation> operations, DbDataReader reader,
        IList<Exception> exceptions,
        CancellationToken token)
    {
        var first = operations.First();

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

        foreach (var operation in operations.Skip(1))
        {
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