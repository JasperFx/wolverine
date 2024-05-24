using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.Acceptance;

public class indefinite_scheduled_retires
{
    [Fact]
    public async Task should_indefinitively_retry_command()
    {
        IndefiniteRetriesHandler.CalledCount = 0;
        using var cts = new CancellationTokenSource(5.Seconds());
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Policies.OnException<IndefiniteRetryException>().ScheduleRetryIndefinitely(100.Milliseconds()))
            .StartAsync();

        var messageBus = host.MessageBus();
        await messageBus.SendAsync(new IndefiniteRetriesCommand(cts, SucceedAfterAttempts: 5));
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }
        IndefiniteRetriesHandler.CalledCount.ShouldBe(5);
    }
    [Fact]
    public async Task should_indefinitively_retry_command_when_given_multiple_delays()
    {
        IndefiniteRetriesHandler.CalledCount = 0;
        using var cts = new CancellationTokenSource(5.Seconds());
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Policies.OnException<IndefiniteRetryException>().ScheduleRetryIndefinitely(50.Milliseconds(), 100.Milliseconds()))
            .StartAsync();

        var messageBus = host.MessageBus();
        await messageBus.SendAsync(new IndefiniteRetriesCommand(cts, SucceedAfterAttempts: 5));
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }
        IndefiniteRetriesHandler.CalledCount.ShouldBe(5);
    }
    
    [Fact]
    public async Task should_stop_retrying_after_cancellation()
    {
        IndefiniteRetriesHandler.CalledCount = 0;
        using var cts = new CancellationTokenSource(5.Seconds());
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Policies.OnException<IndefiniteRetryException>().ScheduleRetryIndefinitely(100.Milliseconds()))
            .StartAsync();

        var messageBus = host.MessageBus();
        await messageBus.SendAsync(new IndefiniteRetriesCommand(cts, CancelAndFailAfterAttempts: 3));
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }
        IndefiniteRetriesHandler.CalledCount.ShouldBe(3);
    }
}

public static class IndefiniteRetriesHandler
{
    public static int CalledCount;
    public static void Handle(IndefiniteRetriesCommand command)
    {
        CalledCount++;
        if (command.SucceedAfterAttempts.HasValue && CalledCount == command.SucceedAfterAttempts.Value)
        {
            command.Cts.Cancel();
        }
        else if (command.CancelAndFailAfterAttempts.HasValue && CalledCount == command.CancelAndFailAfterAttempts.Value)
        {
            command.Cts.Cancel();
            throw new IndefiniteRetryException();
        }
        else
        {
            throw new IndefiniteRetryException();
        }
    }
}

public record IndefiniteRetriesCommand(CancellationTokenSource Cts, int? SucceedAfterAttempts = null, int? CancelAndFailAfterAttempts = null);

public class IndefiniteRetryException : Exception;

