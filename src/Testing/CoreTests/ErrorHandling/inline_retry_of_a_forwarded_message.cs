using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.ErrorHandling;

// GH-4743. ForwardingHandler<T, TDestination> wrote the transformed message back onto the envelope --
// which is how the inner handler receives it -- and nothing ever restored the original. The inline retry
// loop in Executor.InvokeInlineAsync re-enters the same handler on the same envelope, so attempt 2 found
// TDestination sitting where T was expected and the unconditional As<T>() threw:
//
//     System.InvalidCastException : Unable to cast object of type 'ForwardedDestination'
//     to type 'ForwardedOriginal'.
//
// That exception matches no retry policy, so it escaped as-is: the caller of InvokeAsync got a cast error
// instead of either the retry succeeding or the original transient failure. Any IForwardsTo<> message with
// an inline retry policy was affected.
public class inline_retry_of_a_forwarded_message
{
    [Fact]
    public async Task the_retry_reaches_the_destination_handler_again()
    {
        var attempts = new ForwardedAttemptCounter();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(ForwardedDestinationHandler));
                opts.Services.AddSingleton(attempts);

                // 6.0: forwarders are registered explicitly rather than discovered. See #2757.
                opts.RegisterMessageForwarder<ForwardedOriginal, ForwardedDestination>();

                opts.Policies.OnException<ForwardedTransientException>().RetryOnce();
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity()
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(async Task (IMessageContext c) =>
            {
                await c.InvokeAsync(new ForwardedOriginal("James", "Worthy"));
            });

        // Two attempts, and the second one got all the way to the destination handler with the transformed
        // message rather than dying in ForwardingHandler on the way in
        attempts.Count.ShouldBe(2);
        attempts.LastName.ShouldBe("James Worthy");
    }
}

public record ForwardedOriginal(string FirstName, string LastName) : IForwardsTo<ForwardedDestination>
{
    public ForwardedDestination Transform() => new($"{FirstName} {LastName}");
}

public record ForwardedDestination(string FullName);

public class ForwardedTransientException : Exception;

public class ForwardedAttemptCounter
{
    private int _count;
    public int Count => _count;
    public string? LastName { get; set; }
    public int Next() => Interlocked.Increment(ref _count);
}

[WolverineIgnore]
public static class ForwardedDestinationHandler
{
    public static void Handle(ForwardedDestination message, ForwardedAttemptCounter attempts)
    {
        attempts.LastName = message.FullName;

        if (attempts.Next() == 1)
        {
            throw new ForwardedTransientException();
        }
    }
}
