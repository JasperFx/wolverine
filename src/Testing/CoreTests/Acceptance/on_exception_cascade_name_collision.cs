using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// The catch block an OnException middleware generates is emitted *inside* the scope that already
// declares the chain's message body variable, and both names are derived from their type. So when an
// OnException returns the same type a chain handles, the generated handler declared
//
//     var collidingCascade = (CollidingCascade)context.Envelope.Message;
//     try { ... }
//     catch (CollisionException e) { var collidingCascade = middleware.OnException(e); ... }
//
// and failed to compile with CS0136. The handler for that message type then never ran at all.
//
// This was silent because the cascading message is still *sent* -- only its execution is missing --
// so the existing coverage, which asserts on session.Sent, stayed green. This test asserts the
// cascaded message is actually HANDLED.
public class on_exception_cascade_name_collision
{
    [Fact]
    public async Task cascaded_message_of_the_same_type_as_a_chain_is_handled()
    {
        var recorder = new CollisionRecorder();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(recorder);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CollisionTriggerHandler))
                    .IncludeType(typeof(CollidingCascadeHandler));
                opts.Policies.AddMiddleware(typeof(CollidingCascadeMiddleware));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new CollisionTrigger("go"));

        // Before the fix the CollidingCascade chain failed to compile at runtime, so this never ran
        recorder.Handled.ShouldContain("cascaded:go");
    }
}

public class CollisionRecorder
{
    public List<string> Handled { get; } = new();
}

public class CollisionException : Exception
{
    public CollisionException(string message) : base(message)
    {
    }
}

public record CollisionTrigger(string Text);

public record CollidingCascade(string Text);

public static class CollisionTriggerHandler
{
    public static void Handle(CollisionTrigger message)
    {
        throw new CollisionException(message.Text);
    }
}

public static class CollidingCascadeHandler
{
    public static void Handle(CollidingCascade message, CollisionRecorder recorder)
    {
        recorder.Handled.Add(message.Text);
    }
}

public static class CollidingCascadeMiddleware
{
    // Returns the very type CollidingCascadeHandler handles -- that is the collision
    public static CollidingCascade OnException(CollisionException ex)
    {
        return new CollidingCascade($"cascaded:{ex.Message}");
    }
}
