using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;

namespace SqlServerTests.Transport;

/// <summary>
/// SQL Server is the provider the reverted reset-hook approach could never cover (#3554), because
/// its queue tables are not registered on the message store and so a store-side reset could not
/// reach them. Going through <c>SetupAsync</c>/<c>PurgeAsync</c> on the endpoint instead, it comes
/// along for free with no provider-specific code. Resolves #3554.
/// </summary>
[Collection("sqlserver")]
public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    private const string SchemaName = "clear_all_storage";

    protected override async Task beforeHostAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, SchemaName,
            transportSchema: SchemaName);

        // Subscriber only -- no listener, so nothing drains the queue out from under the
        // assertions before the reset runs.
        options.PublishAllMessages().ToSqlServerQueue(QueueName);
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        return ((SqlServerQueue)theQueue).SendAsync(envelope);
    }
}
