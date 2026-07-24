using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Wolverine.RDBMS.Polling;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    async Task IDatabaseOperationBatchExecutor.ExecuteDatabaseOperationBatchAsync(
        IDatabaseOperation[] operations,
        CancellationToken cancellationToken)
    {
        await using var connection =
            (OracleConnection)await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (OracleTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var operation in operations)
            {
                await executeOperationAsync(
                    connection,
                    transaction,
                    operation,
                    operations,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch
            {
                // Preserve the database operation failure that triggered the rollback.
            }

            throw;
        }
    }

    private static async Task executeOperationAsync(
        OracleConnection connection,
        OracleTransaction transaction,
        IDatabaseOperation operation,
        IDatabaseOperation[] operations,
        CancellationToken cancellationToken)
    {
        var builder = new DbCommandBuilder(connection);
        operation.ConfigureCommand(builder);

        await using var command = (OracleCommand)builder.Compile();
        command.BindByName = true;
        command.Transaction = transaction;

        normalizeParameters(command);

        var statements = splitStatements(command.CommandText);

        if (statements.Length == 0) return;

        try
        {
            if (operation is IDoNotReturnData)
            {
                foreach (var statement in statements)
                {
                    command.CommandText = normalizeParameterMarkers(statement);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                return;
            }

            if (statements.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Oracle database operation '{operation}' returns data and must contain exactly one SQL statement.");
            }

            command.CommandText = normalizeParameterMarkers(statements[0]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var exceptions = new List<Exception>();
            await operation.ReadResultsAsync(reader, exceptions, cancellationToken);
            await reader.CloseAsync();
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new DatabaseBatchCommandException(command, operations, e);
        }
    }

    internal static string normalizeParameterMarkers(string sql)
    {
        return Regex.Replace(sql, @"@(?=[A-Za-z_])", ":");
    }

    internal static string[] splitStatements(string sql)
    {
        return sql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static void normalizeParameters(OracleCommand command)
    {
        foreach (OracleParameter parameter in command.Parameters)
        {
            switch (parameter.Value)
            {
                case bool boolean:
                    parameter.OracleDbType = OracleDbType.Int16;
                    parameter.Value = boolean ? 1 : 0;
                    break;

                case Guid guid:
                    parameter.OracleDbType = OracleDbType.Raw;
                    parameter.Value = guid.ToByteArray();
                    break;
            }
        }
    }
}
