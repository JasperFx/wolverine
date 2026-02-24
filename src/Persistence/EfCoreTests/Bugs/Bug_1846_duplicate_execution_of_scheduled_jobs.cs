using Humanizer;
using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace EfCoreTests.Bugs;

[Collection("sqlserver")]
public class Bug_1846_duplicate_execution_of_scheduled_jobs
{
    private readonly ITestOutputHelper _output;

    public Bug_1846_duplicate_execution_of_scheduled_jobs(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Fact]
    public async Task should_not_double_execute()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<ILoggerProvider>(
                    new Wolverine.ComplianceTests.OutputLoggerProvider(_output));
                
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "scheduled");
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
                
                opts.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(300);
                opts.Durability.KeepAfterMessageHandling = TimeSpan.FromMinutes(5);

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();
                opts.UseEntityFrameworkCoreTransactions();
                
                opts.Services.AddDbContextWithWolverineIntegration<CleanDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));
            }).StartAsync();
        
        var session = await host.TrackActivity()
            .WaitForMessageToBeReceivedAt<MsgB>(host)
            .Timeout(1.Minutes())
            .DoNotAssertOnExceptionsDetected()
            .InvokeMessageAndWaitAsync(new Msg0(Guid.NewGuid()));

        if (session.AllExceptions().Any())
        {
            throw new AggregateException(session.AllExceptions());
        }

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

    public static async Task<ScheduledMessage<MsgB>> Handle(MsgA msg, ILogger logger, CleanDbContext dbContext)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Task.Delay(1);
        logger.LogInformation("Recv message A with ID {Id}", msg.MsgId);
        return new MsgB(msg.MsgId, Interlocked.Increment(ref CounterA)).ScheduledAt(now);
    }

    public static async Task Handle(MsgB msg, ILogger logger, CleanDbContext dbContext)
    {
        var value = Interlocked.Increment(ref CounterB);
        if (value != msg.Count)
            throw new NotSupportedException($"Count mismatch (expecting {value}, got {msg.Count}), event evaluated twice");

        logger.LogInformation("Recv message B with ID {Id}: count: {Count}", msg.MsgId, msg.Count);

        await Task.Delay(1000);
    }
}