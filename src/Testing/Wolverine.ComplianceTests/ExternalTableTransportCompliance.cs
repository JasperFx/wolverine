using System.Data.Common;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Transport;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests;

public abstract class ExternalTableTransportCompliance : IAsyncLifetime
{
    protected ITestOutputHelper Output { get; }

    public ExternalTableTransportCompliance(ITestOutputHelper output)
    {
        Output = output;
    }

    protected abstract string connectionString { get; }
    protected abstract string idColumnType { get; }
    protected abstract string bodyColumnType { get; }
    protected abstract string timestampColumnType { get; }
    protected abstract string messageTypeColumnType { get; }
    private string _idColumnName => "id";
    private string _bodyColumnName => "body";
    private string _timestampColumnName => "timestamp";
    private string _externalSchemaName => "ext";


    protected abstract void configurePersistence(WolverineOptions opts, string connectionString, string schemaName);


    public async ValueTask InitializeAsync()
    {
        await dropSchemaAsync(connectionString, [_externalSchemaName], TestContext.Current.CancellationToken);
    }


    protected abstract ValueTask dropSchemaAsync(string connectionString, string[] schemas, CancellationToken cancellationToken);


    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;


    private async Task<IHost> CreateHostBuilder(ExternalMessageTable? table, CancellationToken token)
    {
        var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddSingleton(Output))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                opts.Discovery.IncludeType(typeof(ExternalTableTransportComplianceHandler));
                configurePersistence(opts, connectionString, "wolverine");
                if (table != null)
                {
                    opts.ListenForMessagesFromExternalDatabaseTable(table.TableName.Schema,
                        table.TableName.Name, t =>
                        {
                            t.IdColumnName = table.IdColumnName;
                            t.JsonBodyColumnName = table.JsonBodyColumnName;
                            t.MessageTypeColumnName = table.MessageTypeColumnName;
                            t.TimestampColumnName = table.TimestampColumnName;
                            t.MessageType = table.MessageType;
                            t.PollingInterval = 1.Seconds();
                        });
                }
            }).StartAsync(token);

        await host.ResetResourceState(token);
        return host;
    }


    #region Migrate Table Tests

    [Fact]
    public async Task can_create_basic_table()
    {
        var definition = new ExternalMessageTable(new DbObjectName(_externalSchemaName, "incoming1"))
        {
            MessageType = typeof(Message1)
        };

        await TestMigration(definition,
            [_idColumnName, _bodyColumnName, _timestampColumnName],
            [idColumnType, bodyColumnType, timestampColumnType],
            TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task can_create_basic_table_with_message_type()
    {
        var definition = new ExternalMessageTable(new DbObjectName(_externalSchemaName, "incoming1"))
        {
            MessageType = typeof(Message1),
            MessageTypeColumnName = "message_type"
        };

        await TestMigration(definition,
            [_idColumnName, _bodyColumnName, _timestampColumnName, "message_type"],
            [idColumnType, bodyColumnType, timestampColumnType, messageTypeColumnType],
            TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task can_create_basic_table_customized_in_every_way()
    {
        var definition = new ExternalMessageTable(new DbObjectName(_externalSchemaName, "incoming1"))
        {
            MessageType = typeof(Message1),
            IdColumnName = "pk",
            TimestampColumnName = "added",
            JsonBodyColumnName = "message_body",
            MessageTypeColumnName = "message_kind",
        };

        await TestMigration(definition,
            ["pk", "message_body", "added", "message_kind"],
            [idColumnType, bodyColumnType, timestampColumnType, messageTypeColumnType],
            TestContext.Current.CancellationToken);
    }


    private async Task TestMigration(ExternalMessageTable definition, string[] expectedColumnNames, string[] expectedColumnTypes, CancellationToken token)
    {
        using var host = await CreateHostBuilder(null, token);
        var storage = host.Services.GetRequiredService<IMessageStore>()
            .As<IExternalDbTransportStore>();
        var table = storage.AddExternalMessageTable(definition);
        table.Columns.Select(x => x.Name).ShouldBe(expectedColumnNames);
        table.Columns.Select(x => x.Type).ShouldBe(expectedColumnTypes);
        table.PrimaryKeyColumns.Single().ShouldBe(expectedColumnNames[0]);
        await storage.MigrateExternalMessageTable(definition);

        await using var conn = await storage.DataSource.OpenConnectionAsync();
        var migration = await determineMigrationAsync(conn, token, table);
        migration.Difference.ShouldBe(SchemaPatchDifference.None);
        await host.StopAsync();
    }


    protected virtual Task<SchemaMigration> determineMigrationAsync(DbConnection conn, CancellationToken token, ITable table)
    {
        return SchemaMigration.DetermineAsync(conn, token, table);
    }

    #endregion


    #region End to End Tests

    [Fact]
    public async Task end_to_end_default_message_type()
    {
        var table = new ExternalMessageTable(new DbObjectName(_externalSchemaName, "incoming1"))
        {
            MessageType = typeof(Message1)
        };

        await TestMessageSend(table, new Message1(), $"{_externalSchemaName}.incoming1", TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task end_to_end_default_variable_message_types()
    {
        var table = new ExternalMessageTable(new DbObjectName(_externalSchemaName, "incoming2"))
        {
            MessageTypeColumnName = "message_type",
        };

        await TestMessageSend(table, new Message2(), $"{_externalSchemaName}.incoming2", TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task end_to_end_default_variable_message_types_customize_table_in_every_possible_way()
    {
        var table = new ExternalMessageTable(new DbObjectName(_externalSchemaName, "incoming3"))
        {
            IdColumnName = "pk",
            TimestampColumnName = "added",
            JsonBodyColumnName = "message_body",
            MessageTypeColumnName = "message_kind",
        };

        await TestMessageSend(table, new Message3(), $"{_externalSchemaName}.incoming3", TestContext.Current.CancellationToken);
    }


    private async Task TestMessageSend<T>(ExternalMessageTable table, T message, 
        string endpointName, CancellationToken token)
        where T : notnull
    {
        using var host = await CreateHostBuilder(table, token);

        var tracked = await host.TrackActivity()
            .Timeout(1.Minutes())
            .WaitForMessageToBeReceivedAt<T>(host)
            .ExecuteAndWaitAsync(_ => host.SendMessageThroughExternalTable(endpointName, message));

        var envelope = tracked.Received.SingleEnvelope<T>();
        envelope.Destination.ShouldBe(new Uri($"external-table://{endpointName}/"));
        await host.StopAsync(token);
    }

    #endregion
}


public class ExternalTableTransportComplianceHandler(ITestOutputHelper output)
{
    public void Handle(Message1 message)
    {
        output.WriteLine("Got a Message1: {0}", message);
    }

    public void Handle(Message2 message)
    {
        output.WriteLine("Got a Message2: {0}", message);
    }

    public void Handle(Message3 message)
    {
        output.WriteLine("Got a Message3: {0}", message);
    }
}