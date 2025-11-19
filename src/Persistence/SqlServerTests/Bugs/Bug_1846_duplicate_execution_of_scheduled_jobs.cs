using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests.Bugs;

public class Bug_1846_duplicate_execution_of_scheduled_jobs
{
    [Fact]
    public async Task should_not_double_execute()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "scheduled");
            }).StartAsync();
        
        var session = await host.TrackActivity()
            .WaitForMessageToBeReceivedAt<MsgB>(host)
            .DoNotAssertOnExceptionsDetected()
            .InvokeMessageAndWaitAsync(new Msg0(Guid.NewGuid()));

        session.AllExceptions().Count.ShouldBe(0);

        session.FindSingleTrackedMessageOfType<MsgA>();
        session.FindSingleTrackedMessageOfType<MsgB>(MessageEventType.MessageSucceeded);
    }
}

public sealed record Msg0(Guid MsgId);
public sealed record MsgA(Guid MsgId);
public sealed record MsgB(Guid MsgId, int Count);

public static class TestHandler
{
    private static int CounterA;
    private static int CounterB;

    public static MsgA Handle(Msg0 msg)
    {
        return new(msg.MsgId);
    }

    public static async Task<ScheduledMessage<MsgB>> Handle(MsgA msg, ILogger logger)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Task.Delay(1);
        logger.LogInformation("Recv message A with ID {Id}", msg.MsgId);
        return new MsgB(msg.MsgId, Interlocked.Increment(ref CounterA)).ScheduledAt(now);
    }

    public static async Task Handle(MsgB msg, ILogger logger)
    {
        var value = Interlocked.Increment(ref CounterB);
        if (value != msg.Count)
            throw new NotSupportedException($"Count mismatch (expecting {value}, got {msg.Count}), event evaluated twice");

        logger.LogInformation("Recv message B with ID {Id}: count: {Count}", msg.MsgId, msg.Count);

        await Task.Delay(1000);
    }
}