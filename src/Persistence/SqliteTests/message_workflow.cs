using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Sqlite;

namespace SqliteTests;

[Collection("sqlite")]
public class message_workflow
{
    [Fact]
    public async Task processes_message_through_durable_local_queue()
    {
        using var database = Servers.CreateDatabase("message_workflow");
        var audit = new FileBasedMessageAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            var message = new RegistrationSubmitted(Guid.NewGuid().ToString("N"), "nina@example.com");
            await host.SendAsync(message);

            var received = await audit.ReceivedMessage.Task.WaitAsync(10.Seconds());

            received.UserId.ShouldBe(message.UserId);
            received.Email.ShouldBe("nina@example.com");
            File.Exists(database.DatabaseFile).ShouldBeTrue();
        }
        finally
        {
            await stopHost(host);
        }
    }

    [Fact]
    public async Task scheduled_local_message_survives_host_restart()
    {
        using var database = Servers.CreateDatabase("message_workflow");
        var audit = new FileBasedMessageAudit();
        IHost? firstHost = null;
        IHost? secondHost = null;

        try
        {
            firstHost = await startHost(database.ConnectionString, audit);

            var message = new RegistrationSubmitted(Guid.NewGuid().ToString("N"), "marco@example.com");
            await firstHost.SendAsync(message, new DeliveryOptions { ScheduleDelay = 3.Seconds() });

            await Task.Delay(300.Milliseconds());
            audit.ReceivedMessage.Task.IsCompleted.ShouldBeFalse();

            await stopHost(firstHost);
            firstHost = null;

            secondHost = await startHost(database.ConnectionString, audit);

            var received = await audit.ReceivedMessage.Task.WaitAsync(10.Seconds());
            received.UserId.ShouldBe(message.UserId);
            received.Email.ShouldBe("marco@example.com");
        }
        finally
        {
            await stopHost(firstHost);
            await stopHost(secondHost);
        }
    }

    private static async Task<IHost> startHost(string connectionString, FileBasedMessageAudit audit)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FileBasedMessageHandlers));

                opts.PersistMessagesWithSqlite(connectionString);
                opts.LocalQueue("registrations").UseDurableInbox();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();

                opts.Services.AddSingleton(audit);
            }).StartAsync();
    }

    private static async Task stopHost(IHost? host)
    {
        if (host == null) return;

        await host.StopAsync();
        host.Dispose();
    }

}

[LocalQueue("registrations")]
public record RegistrationSubmitted(string UserId, string Email);

public class FileBasedMessageAudit
{
    public ConcurrentQueue<RegistrationSubmitted> Received { get; } = new();
    public TaskCompletionSource<RegistrationSubmitted> ReceivedMessage { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public class FileBasedMessageHandlers
{
    public static void Handle(RegistrationSubmitted message, FileBasedMessageAudit audit)
    {
        audit.Received.Enqueue(message);
        audit.ReceivedMessage.TrySetResult(message);
    }
}
