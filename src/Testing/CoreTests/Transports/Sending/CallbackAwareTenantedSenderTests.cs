using NSubstitute;
using Shouldly;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

/// <summary>
/// GH-4820. TenantedSender deliberately does not forward an ISenderCallback, because GH-2361 showed that
/// doing so over fire-and-forget senders left outbox rows undeleted forever. This subclass exists for the
/// opposite case -- senders that settle the outbox themselves -- and the whole point is that every one of
/// them actually receives the callback. A sender that silently never got one fails the way BatchedSender
/// does: "This sender has not been registered", thrown inside its own block on a worker thread nobody
/// observes, with the send simply never happening (GH-4073).
/// </summary>
public class CallbackAwareTenantedSenderTests
{
    private readonly Uri theDestination = new("mqtt://topic/outgoing");

    private static ICallbackSender callbackSender()
    {
        var sender = Substitute.For<ICallbackSender>();
        return sender;
    }

    [Fact]
    public void forwards_the_callback_to_the_default_sender_and_every_tenant()
    {
        var defaultSender = callbackSender();
        var one = callbackSender();
        var two = callbackSender();

        var sender = new CallbackAwareTenantedSender(theDestination, TenantedIdBehavior.FallbackToDefault,
            defaultSender);
        sender.RegisterSender("one", one);
        sender.RegisterSender("two", two);

        var callback = Substitute.For<ISenderCallback>();
        sender.RegisterCallback(callback);

        defaultSender.Received(1).RegisterCallback(callback);
        one.Received(1).RegisterCallback(callback);
        two.Received(1).RegisterCallback(callback);
    }

    [Fact]
    public void it_is_an_ISenderRequiresCallback_so_the_agent_pairing_finds_it()
    {
        // EndpointCollection.CreateSendingAgent decides whether to hand over a callback with a type test,
        // so this is the assertion that the forwarding above is ever reached at all
        new CallbackAwareTenantedSender(theDestination, TenantedIdBehavior.FallbackToDefault, callbackSender())
            .ShouldBeAssignableTo<ISenderRequiresCallback>();
    }

    // Mixing the two kinds underneath one of these would reintroduce GH-2361 for the fire-and-forget half:
    // SendingAgent would take the callback-handling path, and the plain sender would never mark anything
    // successful. Refused loudly rather than left to show up as messages that stay in the outbox.
    [Fact]
    public void refuses_a_sender_that_cannot_take_a_callback()
    {
        var sender = new CallbackAwareTenantedSender(theDestination, TenantedIdBehavior.FallbackToDefault,
            callbackSender());
        sender.RegisterSender("plain", Substitute.For<ISender>());

        var ex = Should.Throw<InvalidOperationException>(() =>
            sender.RegisterCallback(Substitute.For<ISenderCallback>()));

        ex.Message.ShouldContain(nameof(ISenderRequiresCallback));
    }

    [Fact]
    public void the_base_class_still_does_not_require_a_callback()
    {
        // The GH-2361 behavior has to stay exactly as it is for every fire-and-forget transport
        new TenantedSender(theDestination, TenantedIdBehavior.FallbackToDefault, Substitute.For<ISender>())
            .ShouldNotBeAssignableTo<ISenderRequiresCallback>();
    }
}

// Top-level and public because Castle DynamicProxy cannot proxy a nested private interface in an
// assembly that is not strong-named.
public interface ICallbackSender : ISender, ISenderRequiresCallback;
