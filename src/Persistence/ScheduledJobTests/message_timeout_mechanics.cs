using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace ScheduledJobTests;

public class message_timeout_mechanics
{
    public static async Task set_default_timeout()
    {
        #region sample_set_default_timeout

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.DefaultExecutionTimeout = 1.Minutes(); }).StartAsync();

        #endregion
    }

    [Fact]
    public void set_timeout_on_handler_attribute()
    {
        using var host = WolverineHost.Basic();
        var handlers = host.Services.GetRequiredService<HandlerGraph>();
        var chain = handlers.HandlerFor(typeof(PotentiallySlowMessage)).As<MessageHandler>().Chain;
        chain.ExecutionTimeoutInSeconds.ShouldBe(1); // coming from the attribute
    }

    [Fact]
    public async Task no_timeout()
    {
        PotentiallySlowMessageHandler.DidTimeout = false; // start clean

        using var host = WolverineHost.Basic();

        await host.TrackActivity().PublishMessageAndWaitAsync(new DurationMessage { DurationInMilliseconds = 50 });

        PotentiallySlowMessageHandler.DidTimeout.ShouldBeFalse();
    }

    [Fact] // This test blinks sometimes when running with other tests
    public async Task timeout_using_global_timeout()
    {
        PotentiallySlowMessageHandler.DidTimeout = false; // start clean

        using var host = WolverineHost.For(opts => { opts.DefaultExecutionTimeout = 50.Milliseconds(); });

        var session = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new DurationMessage { DurationInMilliseconds = 500 });

        var exceptions = session.AllExceptions();
        exceptions.Single().ShouldBeOfType<TaskCanceledException>();
    }

    [Fact] // This test blinks sometimes when running with other tests
    public async Task timeout_using_message_specific_timeout()
    {
        PotentiallySlowMessageHandler.DidTimeout = false; // start clean

        using var host = WolverineHost.Basic();

        var session = await host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .PublishMessageAndWaitAsync(new PotentiallySlowMessage { DurationInMilliseconds = 2500 });

        var exceptions = session.AllExceptions();
        exceptions.Single().ShouldBeOfType<TaskCanceledException>();
    }
}

public class PotentiallySlowMessage
{
    public int DurationInMilliseconds { get; set; }
}

public class DurationMessage
{
    public int DurationInMilliseconds { get; set; }
}

public class PotentiallySlowMessageHandler
{
    public static bool DidTimeout { get; set; }

    #region sample_MessageTimeout_on_handler

    [MessageTimeout(1)]
    public async Task Handle(PotentiallySlowMessage message, CancellationToken cancellationToken)

        #endregion

    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        try
        {
            while (stopwatch.ElapsedMilliseconds < message.DurationInMilliseconds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    DidTimeout = true;
                    break;
                }

                await Task.Delay(25, cancellationToken);
            }
        }
        finally
        {
            stopwatch.Stop();
        }

        DidTimeout = cancellationToken.IsCancellationRequested;
    }

    public async Task Handle(DurationMessage message, CancellationToken cancellationToken)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        try
        {
            while (stopwatch.ElapsedMilliseconds < message.DurationInMilliseconds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    DidTimeout = true;
                    break;
                }

                await Task.Delay(25, cancellationToken);
            }
        }
        finally
        {
            stopwatch.Stop();
        }

        DidTimeout = cancellationToken.IsCancellationRequested;
    }
}