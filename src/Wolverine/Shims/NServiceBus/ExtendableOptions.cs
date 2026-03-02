namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// Base class for NServiceBus-compatible message options.
/// Maps internally to Wolverine's <see cref="DeliveryOptions"/>.
/// </summary>
public abstract class ExtendableOptions
{
    private Dictionary<string, string>? _headers;

    /// <summary>
    /// Sets a header key/value pair on the outgoing message.
    /// </summary>
    public void SetHeader(string key, string value)
    {
        _headers ??= new Dictionary<string, string>();
        _headers[key] = value;
    }

    /// <summary>
    /// Gets all headers set on this options instance.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetHeaders()
    {
        return _headers ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
    }

    /// <summary>
    /// Sets the message identity for this options instance.
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// Converts these NServiceBus-compatible options to Wolverine's <see cref="DeliveryOptions"/>.
    /// </summary>
    internal virtual DeliveryOptions ToDeliveryOptions()
    {
        var options = new DeliveryOptions();

        if (_headers is { Count: > 0 })
        {
            foreach (var kvp in _headers)
            {
                options.Headers[kvp.Key] = kvp.Value;
            }
        }

        return options;
    }
}
