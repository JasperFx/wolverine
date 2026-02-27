using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Sqlite;

namespace SqliteTests.Transport;

[Collection("sqlite")]
public class transport_workflow
{
    [Fact]
    public async Task delivers_message_to_sqlite_queue()
    {
        using var database = Servers.CreateDatabase("transport_workflow");
        var audit = new FileBasedTransportAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            var message = new FileBasedTransportMessage(Guid.NewGuid().ToString("N"), "welcome");
            await sendToQueue(host, message);

            var received = await audit.ReceivedMessage.Task.WaitAsync(10.Seconds());

            received.MessageId.ShouldBe(message.MessageId);
            received.Payload.ShouldBe("welcome");
            File.Exists(database.DatabaseFile).ShouldBeTrue();
        }
        finally
        {
            await stopHost(host);
        }
    }

    [Fact]
    public async Task delivers_scheduled_message_to_sqlite_queue()
    {
        using var database = Servers.CreateDatabase("transport_workflow");
        var audit = new FileBasedTransportAudit();
        IHost? host = null;

        try
        {
            host = await startHost(database.ConnectionString, audit);

            var message = new FileBasedTransportMessage(Guid.NewGuid().ToString("N"), "scheduled");
            await sendToQueue(host, message, 2.Seconds());

            await Task.Delay(300.Milliseconds());
            audit.ReceivedMessage.Task.IsCompleted.ShouldBeFalse();

            var received = await audit.ReceivedMessage.Task.WaitAsync(10.Seconds());
            received.Payload.ShouldBe("scheduled");
        }
        finally
        {
            await stopHost(host);
        }
    }

    [Fact]
    public async Task scheduled_message_survives_host_restart()
    {
        using var database = Servers.CreateDatabase("transport_workflow");
        var audit = new FileBasedTransportAudit();
        IHost? firstHost = null;
        IHost? secondHost = null;

        try
        {
            firstHost = await startHost(database.ConnectionString, audit);

            var message = new FileBasedTransportMessage(Guid.NewGuid().ToString("N"), "after-restart");
            await sendToQueue(firstHost, message, 3.Seconds());

            await Task.Delay(300.Milliseconds());
            audit.ReceivedMessage.Task.IsCompleted.ShouldBeFalse();

            await stopHost(firstHost);
            firstHost = null;

            secondHost = await startHost(database.ConnectionString, audit);

            var received = await audit.ReceivedMessage.Task.WaitAsync(10.Seconds());
            received.MessageId.ShouldBe(message.MessageId);
            received.Payload.ShouldBe("after-restart");
        }
        finally
        {
            await stopHost(firstHost);
            await stopHost(secondHost);
        }
    }

    private static async Task<IHost> startHost(string connectionString, FileBasedTransportAudit audit)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FileBasedTransportHandlers));

                opts.UseSqlitePersistenceAndTransport(connectionString)
                    .AutoProvision();

                opts.ListenToSqliteQueue("inbox").UseDurableInbox();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();

                opts.Services.AddSingleton(audit);
            }).StartAsync();
    }

    private static ValueTask sendToQueue(IHost host, FileBasedTransportMessage message, TimeSpan? delay = null)
    {
        DeliveryOptions? options = null;
        if (delay.HasValue)
        {
            options = new DeliveryOptions { ScheduleDelay = delay };
        }

        return host.MessageBus().EndpointFor("sqlite://inbox".ToUri()).SendAsync(message, options);
    }

    private static async Task stopHost(IHost? host)
    {
        if (host == null) return;

        await host.StopAsync();
        host.Dispose();
    }

}

public record FileBasedTransportMessage(string MessageId, string Payload);

public class FileBasedTransportAudit
{
    public ConcurrentQueue<FileBasedTransportMessage> Received { get; } = new();
    public TaskCompletionSource<FileBasedTransportMessage> ReceivedMessage { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public class FileBasedTransportHandlers
{
    public static void Handle(FileBasedTransportMessage message, FileBasedTransportAudit audit)
    {
        audit.Received.Enqueue(message);
        audit.ReceivedMessage.TrySetResult(message);
    }
}
