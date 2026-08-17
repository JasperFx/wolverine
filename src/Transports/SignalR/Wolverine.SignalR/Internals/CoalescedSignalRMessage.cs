using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolverine.SignalR.Internals;

/// <summary>
///     GH-3972. The wire format for a coalesced batch of outgoing SignalR messages.
/// </summary>
/// <remarks>
///     <para>
///         Sent on a dedicated operation (<see cref="SignalRTransport.CoalescedOperation" />) rather than
///         wrapped into the normal one. A client that does not know about coalescing then simply never receives
///         these, instead of receiving something on <c>ReceiveMessage</c> that it will try to read as a single
///         CloudEvents document and fail on — a silent, per-message failure would be much worse than an obvious
///         "nothing arrived".
///     </para>
///     <para>
///         Each item is a complete CloudEvents document, exactly as it would have been sent on its own. That is
///         what carries the per-item message type: the CloudEvents envelope is per-outer-message, so a wrapper
///         that flattened the items into bare payloads would lose the type of every one of them.
///     </para>
/// </remarks>
public class CoalescedSignalRMessage
{
    /// <summary>
    ///     Marks the payload as a coalesced batch, so a client reading a message it did not expect can tell
    ///     what it is looking at rather than guessing from shape.
    /// </summary>
    [JsonPropertyName("wolverineBatch")]
    public bool WolverineBatch => true;

    /// <summary>
    ///     The coalesced CloudEvents documents, in arrival order.
    /// </summary>
    [JsonPropertyName("items")]
    public string[] Items { get; set; } = [];

    public static string ToJson(IReadOnlyList<string> items, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize(new CoalescedSignalRMessage { Items = items.ToArray() }, options);
    }

    /// <summary>
    ///     Reads a coalesced payload back into its individual CloudEvents documents.
    /// </summary>
    public static bool TryReadItems(string json, JsonSerializerOptions options, out string[] items)
    {
        try
        {
            var message = JsonSerializer.Deserialize<CoalescedSignalRMessage>(json, options);
            if (message?.Items is { Length: > 0 })
            {
                items = message.Items;
                return true;
            }

            // An empty batch is still a well-formed batch; the caller has nothing to do with it
            items = [];
            return message != null;
        }
        catch (JsonException)
        {
            items = [];
            return false;
        }
    }
}
