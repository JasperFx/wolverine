using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using Wolverine.Util;

namespace Wolverine.Transports;

/// <summary>
/// Shared handling for the NServiceBus headers that every Wolverine NServiceBus interop mapper has to read,
/// regardless of transport.
/// </summary>
/// <remarks>
/// GH-3628. Each transport grew its own copy of this logic and they drifted: the database transports split the
/// <c>NServiceBus.EnclosedMessageTypes</c> list correctly, while Azure Service Bus and the AWS transports passed
/// the whole raw header to <see cref="Type.GetType(string)" />. That works only for the simplest possible
/// contract — a message type implementing no interface the NServiceBus conventions also treat as a message type —
/// and throws for anything else, taking the listener down. Sharing one implementation is the point: a transport
/// cannot silently fall behind a fix made for another.
/// </remarks>
internal static class NServiceBusInterop
{
    public const string EnclosedMessageTypesHeader = "NServiceBus.EnclosedMessageTypes";

    /// <summary>
    /// Resolve the Wolverine message type name from an <c>NServiceBus.EnclosedMessageTypes</c> header value.
    /// Returns null when the header carries nothing usable, so callers can leave <c>Envelope.MessageType</c>
    /// alone rather than overwriting it with a guess.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification =
            "The enclosed message type name comes from a foreign NServiceBus endpoint at runtime; a failed resolution falls back to the bare type name which Wolverine binds via RegisterInteropMessageAssembly.")]
    public static string? ResolveMessageType(string? enclosed)
    {
        if (enclosed.IsEmpty())
        {
            return null;
        }

        // NServiceBus writes this as a ';'-delimited, most-derived-first list of every type the message
        // satisfies -- the concrete type followed by any parent types or interfaces that the receiving
        // endpoint's conventions also class as message types (MessageMetadata.SerializeMessageHierarchy is a
        // string.Join(";", ...)). Only one entry at a time is a valid assembly-qualified name.
        var entries = enclosed!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            // Use the first entry that resolves to a type loadable in this process; that is the one Wolverine
            // can map to a handler, either directly or via the RegisterInteropMessageAssembly
            // interface->concrete binding.
            if (tryLoad(entry) is Type type)
            {
                return type.ToMessageTypeName();
            }
        }

        if (entries.Length == 0)
        {
            return null;
        }

        // Nothing resolved. Fall back to the bare type name of the most-derived entry; Wolverine's interop
        // assembly registration can still bind that to a concrete handler. A header carrying no usable name at
        // all yields null rather than an empty MessageType -- an unroutable message belongs in the dead letter
        // queue, not stamped with a blank type.
        var bare = entries[0].Split(',', StringSplitOptions.TrimEntries)[0];
        return bare.IsNotEmpty() ? bare : null;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification = "See ResolveMessageType; the name is foreign runtime data and failure is handled.")]
    private static Type? tryLoad(string typeName)
    {
        try
        {
            return Type.GetType(typeName);
        }
        catch (Exception)
        {
            // Type.GetType does NOT merely return null for a name it cannot make sense of -- a malformed
            // assembly-qualified name throws (FileLoadException "The given assembly name was invalid." is the
            // one GH-3628 was reported against). A junk header must not be able to kill a listener, so treat an
            // unloadable entry exactly like an unresolved one and move on to the next.
            return null;
        }
    }
}
