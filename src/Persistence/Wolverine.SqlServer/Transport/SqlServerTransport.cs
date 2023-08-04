using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Weasel.SqlServer;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

public class SqlServerTransport : BrokerTransport<SqlServerQueue>
{
    public const string ProtocolName = "sqlserver";

    public SqlServerTransport(DatabaseSettings settings) : base(ProtocolName, "Sql Server Transport")
    {
        Queues = new LightweightCache<string, SqlServerQueue>(name => new SqlServerQueue(name, this));
        Settings = settings;
    }

    public LightweightCache<string, SqlServerQueue> Queues { get; }

    protected override IEnumerable<SqlServerQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLower();
    }

    protected override SqlServerQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Segments.Last().Trim('/');
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        var storage = runtime.Storage as SqlServerMessageStore;

        Storage = storage ?? throw new InvalidOperationException(
            "The Sql Server Transport can only be used if the message persistence is also Sql Server backed");
        
        Settings = storage.Settings;

        // This is de facto a little environment test
        await using var conn = new SqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();
        await conn.CloseAsync();
    }

    internal DatabaseSettings Settings { get; set; }

    internal SqlServerMessageStore Storage { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);
        
    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        await using var conn = new SqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();

        return (DateTimeOffset)await conn.CreateCommand("select SYSDATETIMEOFFSET()").ExecuteScalarAsync();
    }
}