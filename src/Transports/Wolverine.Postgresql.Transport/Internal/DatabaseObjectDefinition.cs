using Npgsql;

namespace Wolverine.Transports.Postgresql.Internal;

/// <summary>
/// Defines a contract for creating, dropping, and checking for the existence of a database object.
/// </summary>
public abstract class DatabaseObjectDefinition
{
    /// <summary>
    /// Builds the SQL statement that creates the database object.
    /// </summary>
    /// <returns>The SQL statement that creates the database object.</returns>
    protected abstract string BuildCreateStatement();

    /// <summary>
    /// Builds the SQL statement that drops the database object.
    /// </summary>
    /// <returns>The SQL statement that drops the database object.</returns>
    protected abstract string BuildDropStatement();

    /// <summary>
    /// Builds the SQL statement that checks for the existence of the database object.
    /// </summary>
    /// <returns>The SQL statement that checks for the existence of the database object.</returns>
    protected abstract string BuildExistsStatement();

    /// <summary>
    /// Checks whether the database object exists in the database using the BuildExistsStatement method.
    /// </summary>
    /// <param name="connection">The NpgsqlConnection used to connect to the database.</param>
    /// <param name="cancellationToken">The CancellationToken used to cancel the operation.</param>
    /// <returns>True if the database object exists; otherwise, false.</returns>
    public async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildExistsStatement();

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Creates the database object using the BuildCreateStatement method.
    /// </summary>
    /// <param name="connection">The NpgsqlConnection used to connect to the database.</param>
    /// <param name="cancellationToken">The CancellationToken used to cancel the operation.</param>
    /// <returns>A Task that represents the asynchronous create operation.</returns>
    public async Task CreateAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildCreateStatement();

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Drops the database object using the BuildDropStatement method.
    /// </summary>
    /// <param name="connection">The NpgsqlConnection used to connect to the database.</param>
    /// <param name="cancellationToken">The CancellationToken used to cancel the operation.</param>
    /// <returns>A Task that represents the asynchronous drop operation.</returns>
    public async Task DropAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = BuildDropStatement();

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
