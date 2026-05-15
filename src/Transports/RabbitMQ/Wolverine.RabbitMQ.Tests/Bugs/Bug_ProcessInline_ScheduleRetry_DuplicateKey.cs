using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Wolverine.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_ProcessInline_ScheduleRetry_DuplicateKey_Count2 : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private readonly string _queueName;

    public Bug_ProcessInline_ScheduleRetry_DuplicateKey_Count2(ITestOutputHelper output)
    {
        _output = output;
        _queueName = RabbitTesting.NextQueueName();
    }

    public async Task InitializeAsync()
    {
        ScheduleRetryCount2Handler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "inline_retry2");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();
                opts.PublishMessage<ScheduleRetryCount2Message>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName).ProcessInline();
                opts.LocalRoutingConventionDisabled = true;
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            await _host.TeardownResources();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task process_inline_schedule_retry_count_2_succeeds_on_third_attempt()
    {
        await _host.MessageBus().PublishAsync(new ScheduleRetryCount2Message());

        var success = await Poll(60.Seconds(), () => ScheduleRetryCount2Handler.Succeeded);

        _output.WriteLine($"Total handler invocations: {ScheduleRetryCount2Handler.Attempts.Count}");

        success.ShouldBeTrue("Handler should succeed on the third attempt without a DuplicateIncomingEnvelopeException.");
        ScheduleRetryCount2Handler.Attempts.Count.ShouldBeGreaterThanOrEqualTo(3,
            "Expected at least 3 handler invocations: fail, fail, succeed");
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }
}

public class Bug_ProcessInline_ScheduleRetry_DuplicateKey_Count1 : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host = null!;
    private readonly string _queueName;

    public Bug_ProcessInline_ScheduleRetry_DuplicateKey_Count1(ITestOutputHelper output)
    {
        _output = output;
        _queueName = RabbitTesting.NextQueueName();
    }

    public async Task InitializeAsync()
    {
        ScheduleRetryCount1Handler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "inline_retry1");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();
                opts.PublishMessage<ScheduleRetryCount1Message>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName).ProcessInline();
                opts.LocalRoutingConventionDisabled = true;
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            await _host.TeardownResources();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task process_inline_schedule_retry_count_1_succeeds_on_second_attempt()
    {
        await _host.MessageBus().PublishAsync(new ScheduleRetryCount1Message());

        var success = await Poll(30.Seconds(), () => ScheduleRetryCount1Handler.Succeeded);

        _output.WriteLine($"Total handler invocations: {ScheduleRetryCount1Handler.Attempts.Count}");

        success.ShouldBeTrue("Handler should succeed on the second attempt.");
        ScheduleRetryCount1Handler.Attempts.Count.ShouldBeGreaterThanOrEqualTo(2,
            "Expected at least 2 handler invocations: fail, succeed");
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }
}

public record ScheduleRetryCount2Message;

public static class ScheduleRetryCount2Handler
{
    public static readonly ConcurrentBag<DateTimeOffset> Attempts = new();
    public static volatile bool Succeeded;
    private static int _attemptCount;

    public static void Reset()
    {
        Attempts.Clear();
        Succeeded = false;
        _attemptCount = 0;
    }

    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException()
            .ScheduleRetry(1.Seconds(), 1.Seconds())
            .Then.MoveToErrorQueue();
    }

    public static void Handle(ScheduleRetryCount2Message _)
    {
        Attempts.Add(DateTimeOffset.UtcNow);
        var attempt = Interlocked.Increment(ref _attemptCount);
        if (attempt < 3)
        {
            throw new InvalidOperationException($"Simulated failure on attempt {attempt}");
        }

        Succeeded = true;
    }
}

public record ScheduleRetryCount1Message;

public static class ScheduleRetryCount1Handler
{
    public static readonly ConcurrentBag<DateTimeOffset> Attempts = new();
    public static volatile bool Succeeded;
    private static int _attemptCount;

    public static void Reset()
    {
        Attempts.Clear();
        Succeeded = false;
        _attemptCount = 0;
    }

    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException()
            .ScheduleRetry(1.Seconds())
            .Then.MoveToErrorQueue();
    }

    public static void Handle(ScheduleRetryCount1Message _)
    {
        Attempts.Add(DateTimeOffset.UtcNow);
        var attempt = Interlocked.Increment(ref _attemptCount);
        if (attempt < 2)
        {
            throw new InvalidOperationException($"Simulated failure on attempt {attempt}");
        }

        Succeeded = true;
    }
}