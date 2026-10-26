using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MySqlTests;

public class Bug_5215_reserved_word_schema_name
{
    [Theory]
    [InlineData("order")]
    [InlineData("user")]
    public async Task message_storage_works_in_a_schema_named_after_a_reserved_word(string schemaName)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, schemaName);
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host.TrackActivity().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ReservedSchemaMessage("Tom Landry"));

        tracked.Executed.SingleMessage<ReservedSchemaMessage>().Name.ShouldBe("Tom Landry");

        var runtime = host.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        await new ReleaseOrphanedMessagesCommand(database, runtime.DurabilitySettings, NullLogger.Instance, [0], 0)
            .ExecuteAsync(runtime, TestContext.Current.CancellationToken);

        await new DeleteExpiredHandledEnvelopesCommand(database, runtime.DurabilitySettings, NullLogger.Instance)
            .ExecuteAsync(runtime, TestContext.Current.CancellationToken);

        var agent = new DurabilityAgent(runtime, database);
        await new DatabaseOperationBatch(database, agent.buildOperationBatch())
            .ExecuteAsync(runtime, TestContext.Current.CancellationToken);
    }
}

public record ReservedSchemaMessage(string Name);

public static class ReservedSchemaMessageHandler
{
    public static void Handle(ReservedSchemaMessage message)
    {
    }
}
