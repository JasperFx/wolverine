using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Weasel.SqlServer;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;

namespace Wolverine.SqlServer.Transport;

public class SqlServerTransport : BrokerTransport<SqlServerQueue>
{
    public const string ProtocolName = "sqlserver";

    public SqlServerTransport(DatabaseSettings settings) : this(settings, settings.SchemaName)
    {
        
    }
    public SqlServerTransport(DatabaseSettings settings, string? transportSchemaName) : base(ProtocolName, "Sql Server Transport")
    {
        Queues = new LightweightCache<string, SqlServerQueue>(name => new SqlServerQueue(name, this));
        Settings = settings;
        if (settings.SchemaName.IsNotEmpty())
        {
            TransportSchemaName = settings.SchemaName;
            MessageStorageSchemaName = settings.SchemaName;
        }
        if (transportSchemaName.IsNotEmpty())
        {
            TransportSchemaName = transportSchemaName;
        }
    }

    public LightweightCache<string, SqlServerQueue> Queues { get; }

	/// <summary>
    /// Schema name for the queue and scheduled message tables
    /// </summary>
    public string TransportSchemaName { get; private set; } = "dbo";
    
    /// <summary>
    /// Schema name for the message storage tables
    /// </summary>
    public string MessageStorageSchemaName { get; private set; } = "dbo";

    protected override IEnumerable<SqlServerQueue> endpoints()
    {
        return Queues;
    }

    protected override IEnumerable<Endpoint> explicitEndpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLower();
    }

    protected override SqlServerQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        AutoProvision = AutoProvision || runtime.Options.AutoBuildMessageStorageOnStartup;
        
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