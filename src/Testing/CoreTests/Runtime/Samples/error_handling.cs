using System.Diagnostics;
using System.Net.Sockets;
using System.Security;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace CoreTests.Runtime.Samples;

public class error_handling
{
    public static async Task MyApp_with_error_handling()
    {
        #region sample_MyApp_with_error_handling

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Policies.Add<ErrorHandlingPolicy>(); }).StartAsync();

        #endregion
    }

    public static async Task GlobalErrorHandlingConfiguration()
    {
        #region sample_GlobalErrorHandlingConfiguration

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.OnException<TimeoutException>().ScheduleRetry(5.Seconds());
                opts.Policies.OnException<SecurityException>().MoveToErrorQueue();

                // You can also apply an additional filter on the
                // exception type for finer grained policies
                opts.Policies
                    .OnException<SocketException>(ex => ex.Message.Contains("not responding"))
                    .ScheduleRetry(5.Seconds());
            }).StartAsync();

        #endregion
    }

    public static async Task filtering_by_exception_type()
    {
        #region sample_filtering_by_exception_type

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies
                    .OnException<SqlException>()
                    .Or<InvalidOperationException>(ex => ex.Message.Contains("Intermittent message of some kind"))
                    .OrInner<BadImageFormatException>()

                    // And apply the "continuation" action to take if the filters match
                    .Requeue();

                // Use different actions for different exception types
                opts.Policies.OnException<InvalidOperationException>().RetryTimes(3);
            }).StartAsync();

        #endregion
    }

    public static async Task continuation_actions()
    {
        #region sample_continuation_actions

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Try to execute the message again without going
                // back through the queue up to 5 times
                opts.OnException<SqlException>().RetryTimes(5);

                // Retry with a cooldown up to 3 times, then discard the message
                opts.OnException<TimeoutException>()
                    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
                    .Then.Discard();


                // Retry the message again, but wait for the specified time
                // The message will be dead lettered if it exhausts the delay
                // attempts
                opts
                    .OnException<SqlException>()
                    .ScheduleRetry(3.Seconds(), 10.Seconds(), 20.Seconds());

                // Put the message back into the queue where it will be
                // attempted again
                // The message will be dead lettered if it exceeds the maximum number
                // of attempts
                opts.OnException<SqlException>().Requeue(5);
            }).StartAsync();

        #endregion
    }

    public static async Task send_to_error_queue()
    {
        #region sample_send_to_error_queue

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Don't retry, immediately send to the error queue
                opts.OnException<TimeoutException>().MoveToErrorQueue();
            }).StartAsync();

        #endregion
    }

    public static async Task pause_when_unusable()
    {
        #region sample_pause_when_system_is_unusable

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The failing message is requeued for later processing, then
                // the specific listener is paused for 10 minutes
                opts.OnException<SystemIsCompletelyUnusableException>()
                    .Requeue().AndPauseProcessing(10.Minutes());
            }).StartAsync();

        #endregion
    }

    public static async Task discard_when_unusable()
    {
        #region sample_discard_when_message_is_invalid

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Bad message, get this thing out of here!
                opts.OnException<InvalidMessageYouWillNeverBeAbleToProcessException>()
                    .Discard();
            }).StartAsync();

        #endregion
    }

    public static async Task with_exponential_backoff()
    {
        #region sample_exponential_backoff

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Retry the message again, but wait for the specified time
                // The message will be dead lettered if it exhausts the delay
                // attempts
                opts
                    .OnException<SqlException>()
                    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
            }).StartAsync();

        #endregion
    }

    public static async Task AppWithCustomContinuation()
    {
        #region sample_AppWithCustomContinuation

        throw new NotImplementedException();
        // using var host = Host.CreateDefaultBuilder()
        //     .UseWolverine(opts =>
        //     {
        //         opts.Handlers.OnException<UnauthorizedAccessException>()
        //
        //             // The With() function takes a lambda factory for
        //             // custom IContinuation objects
        //             .With((envelope, exception) => new RaiseAlert(exception));
        //     }).StartAsync();

        #endregion
    }

    public class SystemIsCompletelyUnusableException : Exception;

    public class InvalidMessageYouWillNeverBeAbleToProcessException : Exception;

    #region sample_exponential_backoff_with_attributes

    [RetryNow(typeof(SqlException), 50, 100, 250)]
    public class MessageWithBackoff
    {
        // whatever members
    }

    #endregion
}

#region sample_ErrorHandlingPolicy

// This error policy will apply to all message types in the namespace
// 'MyApp.Messages', and add a "requeue on SqlException" to all of these
// message handlers
public class ErrorHandlingPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var matchingChains = chains
            .Where(x => x.MessageType.IsInNamespace("MyApp.Messages"));

        foreach (var chain in matchingChains) chain.OnException<SqlException>().Requeue(2);
    }
}

#endregion

#region sample_configure_error_handling_per_chain_with_configure

public class MyErrorCausingHandler
{
    // This method signature is meaningful
    public static void Configure(HandlerChain chain)
    {
        // Requeue on IOException for a maximum
        // of 3 attempts
        chain.OnException<IOException>()
            .Requeue();
    }

    public void Handle(InvoiceCreated created)
    {
        // handle the invoice created message
    }

    public void Handle(InvoiceApproved approved)
    {
        // handle the invoice approved message
    }
}

#endregion

public class InvoiceCreated
{
    public DateTime Time { get; set; }
    public string Purchaser { get; set; }
    public double Amount { get; set; }
}

public class InvoiceApproved;

#region sample_configuring_error_handling_with_attributes

public class AttributeUsingHandler
{
    [ScheduleRetry(typeof(IOException), 5)]
    [RetryNow(typeof(SqlException), 50, 100, 250)]
    [RequeueOn(typeof(InvalidOperationException))]
    [MoveToErrorQueueOn(typeof(DivideByZeroException))]
    [MaximumAttempts(2)]
    public void Handle(InvoiceCreated created)
    {
        // handle the invoice created message
    }
}

#endregion

public class SqlException : Exception;

public class FailedOnSecurity
{
    public FailedOnSecurity(string message)
    {
    }
}

#region sample_RaiseAlert_Continuation

public class RaiseAlert : IContinuation
{
    private readonly Exception _ex;

    public RaiseAlert(Exception ex)
    {
        _ex = ex;
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle,
        IWolverineRuntime runtime,
        DateTimeOffset now, Activity activity)
    {
        await lifecycle.SendAsync(new RescheduledAlert
        {
            Id = lifecycle.Envelope.Id,
            ExceptionText = _ex.ToString()
        });
    }
}

#endregion

public class RescheduledAlert
{
    public Guid Id { get; set; }
    public string ExceptionText { get; set; }
}