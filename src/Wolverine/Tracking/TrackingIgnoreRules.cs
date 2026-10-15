using System.Reflection;

namespace Wolverine.Tracking;

/// <summary>
/// Ignore rules for tracked sessions, written once against a host through
/// <see cref="TrackingOptions" /> and applied to every tracked session opened against that host. Each rule is
/// either a message-type rule or an envelope rule; a session ignores an envelope that any rule matches.
/// </summary>
internal sealed class TrackingIgnoreRules
{
    private readonly List<Func<Type, bool>> _messageTypeRules = [];
    private readonly List<Func<Envelope, bool>> _envelopeRules = [];

    public bool IsEmpty => _messageTypeRules.Count == 0 && _envelopeRules.Count == 0;

    public IReadOnlyList<Func<Type, bool>> MessageTypeRules => _messageTypeRules;
    public IReadOnlyList<Func<Envelope, bool>> EnvelopeRules => _envelopeRules;

    public void IgnoreMessagesMatchingType(Func<Type, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _messageTypeRules.Add(filter);
    }

    public void IgnoreEnvelopes(Func<Envelope, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _envelopeRules.Add(filter);
    }

    /// <summary>
    /// A message-type rule for every message type declared in <paramref name="assembly" />. Batched messages
    /// are arrays, and the session matches an array against its element type too, so a batch of these is
    /// covered as well.
    /// </summary>
    public static Func<Type, bool> FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return t => t.Assembly == assembly;
    }

    /// <summary>
    /// An envelope rule for traffic to or through one endpoint: the envelope's destination is that endpoint,
    /// which covers both an envelope sent to it and one received by its listener.
    /// </summary>
    public static Func<Envelope, bool> ForEndpoint(Uri endpointUri)
    {
        ArgumentNullException.ThrowIfNull(endpointUri);

        // A trailing slash is the only spelling difference an endpoint Uri picks up in practice
        var target = endpointUri.ToString().TrimEnd('/');
        return e => e.Destination is not null &&
                    string.Equals(e.Destination.ToString().TrimEnd('/'), target, StringComparison.OrdinalIgnoreCase);
    }
}
