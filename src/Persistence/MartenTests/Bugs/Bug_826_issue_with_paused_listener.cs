using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class Bug_826_issue_with_paused_listener
{
    private async Task dropSchema()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("bug826");
        await conn.CloseAsync();
    }

    [Fact]
    public async Task can_resume_listening()
    {
        await dropSchema();

        var builder = Host.CreateDefaultBuilder();

        builder.UseWolverine(options =>
        {
            options.LocalQueueFor<ThisMeansTrouble>().Sequential();
            options.OnException<Exception>()
                .Requeue().AndPauseProcessing(5.Seconds());

            options.Durability.Mode = DurabilityMode.Solo;

            options.Policies.AutoApplyTransactions();
            options.Policies.UseDurableLocalQueues();
            options.Policies.UseDurableOutboxOnAllSendingEndpoints();

            options.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug826";
                    m.DisableNpgsqlLogging = true;
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();
        });

        using var host = await builder.StartAsync();

        await host.TrackActivity()
            .WaitForMessageToBeReceivedAt<ThisMeansTrouble>(host)
            .Timeout(20.Seconds())
            .PublishMessageAndWaitAsync(new ThisMeansTrouble());
    }
}

public record ThisMeansTrouble();

public class UnreliableHandler
{
    public static bool HasFailed = false;

    public static void Handle(ThisMeansTrouble message, ILogger logger, Envelope envelope)
    {
        logger.LogWarning("Handler called");

        if (HasFailed)
        {
            envelope.Attempts.ShouldBe(2);
            return;
        }
        HasFailed = true;
        throw new Exception();
    }
}