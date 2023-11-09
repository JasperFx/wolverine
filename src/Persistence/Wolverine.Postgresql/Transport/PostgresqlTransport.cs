using JasperFx.Core;
using Npgsql;
using Spectre.Console;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlTransport : BrokerTransport<PostgresqlQueue>
{
    public const string ProtocolName = "sqlserver";

    public PostgresqlTransport(DatabaseSettings settings) : base(ProtocolName, "Sql Server Transport")
    {
        Queues = new LightweightCache<string, PostgresqlQueue>(name => new PostgresqlQueue(name, this));
        Settings = settings;
    }

    public LightweightCache<string, PostgresqlQueue> Queues { get; }

    protected override IEnumerable<PostgresqlQueue> endpoints()
    {
        return Queues;
    }

    public override string SanitizeIdentifier(string identifier)
    {
        return identifier.Replace('-', '_').ToLower();
    }

    protected override PostgresqlQueue findEndpointByUri(Uri uri)
    {
        var queueName = uri.Host;
        return Queues[queueName];
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        var storage = runtime.Storage as PostgresqlMessageStore;

        Storage = storage ?? throw new InvalidOperationException(
            "The Sql Server Transport can only be used if the message persistence is also Sql Server backed");
        
        Settings = storage.Settings;

        // This is de facto a little environment test
        await using var conn = new NpgsqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();
        await conn.CloseAsync();
    }

    internal DatabaseSettings Settings { get; set; }

    internal PostgresqlMessageStore Storage { get; set; }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Name");
        yield return new PropertyColumn("Count", Justify.Right);
        yield return new PropertyColumn("Scheduled", Justify.Right);
        
    }

    public async Task<DateTimeOffset> SystemTimeAsync()
    {
        await using var conn = new NpgsqlConnection(Settings.ConnectionString);
        await conn.OpenAsync();

        return (DateTimeOffset)await conn.CreateCommand("select SYSDATETIMEOFFSET()").ExecuteScalarAsync();
    }

}