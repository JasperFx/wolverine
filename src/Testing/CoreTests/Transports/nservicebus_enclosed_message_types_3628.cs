using Shouldly;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
///     GH-3628. NServiceBus writes <c>NServiceBus.EnclosedMessageTypes</c> as a ';'-delimited,
///     most-derived-first list of every type the published message satisfies — the concrete type, then any
///     parent type or interface the receiving conventions also class as a message type. The Azure Service Bus
///     and AWS interop mappers passed that whole string to <see cref="Type.GetType(string)" />, which accepts a
///     single assembly-qualified name; everything after the first comma is read as assembly metadata, so the
///     listener threw <c>FileLoadException: The given assembly name was invalid.</c> and the message was never
///     dispatched. A contract implementing no qualifying interface produces a one-entry header, which is why
///     the simplest possible message shape did not reproduce it.
/// </summary>
public class nservicebus_enclosed_message_types_3628
{
    // The exact header shape from the report: MyEvent : IMyEvent under
    // conventions.DefiningEventsAs(t => t.IsAssignableTo(typeof(IMyEvent))). Neither assembly exists here,
    // standing in for a foreign NServiceBus endpoint's contracts.
    private const string ForeignHierarchyHeader =
        "MyApp.Messages.MyEvent, MyApp.Messages, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null;" +
        "MyApp.Abstractions.IMyEvent, MyApp.Abstractions, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    [Fact]
    public void the_unsplit_header_is_what_broke_the_listener()
    {
        // Pins the mechanism rather than the fix: Type.GetType does not politely return null for the joined
        // list, it throws. That is why splitting is required and why a null-check alone was never enough.
        Should.Throw<Exception>(() => Type.GetType(ForeignHierarchyHeader));
    }

    [Fact]
    public void a_multi_type_header_no_longer_takes_the_listener_down()
    {
        var resolved = Should.NotThrow(() => NServiceBusInterop.ResolveMessageType(ForeignHierarchyHeader));

        // Nothing in the list is loadable here, so we fall back to the bare name of the most-derived entry.
        // Wolverine binds that to a concrete handler via RegisterInteropMessageAssembly.
        resolved.ShouldBe("MyApp.Messages.MyEvent");
    }

    [Fact]
    public void the_most_derived_loadable_entry_wins()
    {
        // Concrete type first, as NServiceBus orders it. It resolves, so the interface entry is never reached.
        var header = $"{qualify(typeof(NsbInteropEvent))};{qualify(typeof(INsbInteropEvent))}";

        NServiceBusInterop.ResolveMessageType(header)
            .ShouldBe(typeof(NsbInteropEvent).ToMessageTypeName());
    }

    [Fact]
    public void an_unloadable_leading_entry_falls_through_to_the_next()
    {
        // The real cross-assembly interop case: the publisher's concrete type is not deployed here, but the
        // shared interface is. That entry is the one Wolverine can actually map to a handler.
        var header =
            "MyApp.Messages.NotDeployedHere, MyApp.Messages, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null;" +
            qualify(typeof(INsbInteropEvent));

        NServiceBusInterop.ResolveMessageType(header)
            .ShouldBe(typeof(INsbInteropEvent).ToMessageTypeName());
    }

    [Fact]
    public void a_single_unresolvable_entry_yields_its_bare_name_and_not_a_null_reference()
    {
        // The old ASB code did messageType!.ToMessageTypeName() on the result of Type.GetType, so a
        // syntactically valid but unresolvable single type name produced a NullReferenceException.
        NServiceBusInterop
            .ResolveMessageType("MyApp.Messages.Gone, MyApp.Messages, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")
            .ShouldBe("MyApp.Messages.Gone");
    }

    [Fact]
    public void a_malformed_header_does_not_throw()
    {
        // A junk header must dead-end at this message, not kill the listening loop.
        Should.NotThrow(() => NServiceBusInterop.ResolveMessageType("!!! not a type name at all"));
        Should.NotThrow(() => NServiceBusInterop.ResolveMessageType(", , ,"));
        Should.NotThrow(() => NServiceBusInterop.ResolveMessageType(";;;"));
    }

    [Fact]
    public void empty_and_whitespace_padding_are_tolerated()
    {
        // Returning null lets callers leave Envelope.MessageType alone rather than overwrite it with a guess.
        NServiceBusInterop.ResolveMessageType(null).ShouldBeNull();
        NServiceBusInterop.ResolveMessageType("").ShouldBeNull();
        NServiceBusInterop.ResolveMessageType(";;;").ShouldBeNull();

        // Padding and empty entries around a good name must not defeat resolution.
        NServiceBusInterop.ResolveMessageType($" ; {qualify(typeof(NsbInteropEvent))} ; ")
            .ShouldBe(typeof(NsbInteropEvent).ToMessageTypeName());
    }

    private static string qualify(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    public interface INsbInteropEvent;

    public record NsbInteropEvent(string Name) : INsbInteropEvent;
}
