namespace Wolverine.Transports.Postgresql.Internal;

internal static class PostgresClientExtensions
{
    public static async Task<bool> ExistsAsync(
        this PostgresClient client,
        DatabaseObjectDefinition definition,
        CancellationToken cancellationToken)
    {
        await using var connection = await client.DataSource.OpenConnectionAsync(cancellationToken);

        return await definition.ExistsAsync(connection, cancellationToken);
    }

    public static async Task CreateAsync(
        this PostgresClient client,
        DatabaseObjectDefinition definition,
        CancellationToken cancellationToken)
    {
        await using var connection = await client.DataSource.OpenConnectionAsync(cancellationToken);

        await definition.CreateAsync(connection, cancellationToken);
    }

    public static async Task DropAsync(
        this PostgresClient client,
        DatabaseObjectDefinition definition,
        CancellationToken cancellationToken)
    {
        await using var connection = await client.DataSource.OpenConnectionAsync(cancellationToken);

        await definition.DropAsync(connection, cancellationToken);
    }
}
