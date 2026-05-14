namespace Wolverine.Runtime.Agents;

/// <summary>
/// Round-trippable encoding for the agent URI carried inside the cluster's
/// <see cref="NodeAgentController"/> when a listener URI is registered via
/// <see cref="Persistence.Durability.IListenerStore"/>.
///
/// The agent URI is a single-segment hierarchical Uri whose path is the
/// percent-encoded listener URI. Concrete example:
///
///   listener: <c>mqtt://broker:1883/devices/foo/status</c>
///   agent:    <c>wolverine-dynamic-listener:///mqtt%3A%2F%2Fbroker%3A1883%2Fdevices%2Ffoo%2Fstatus</c>
///
/// The empty authority (<c>:///</c>) keeps the encoded payload entirely inside
/// the path component, so a listener URI with embedded scheme delimiters
/// can't collide with the agent URI's own scheme/host parsing. Decoding is
/// the symmetric <see cref="Uri.UnescapeDataString(string)"/> operation.
/// </summary>
internal static class DynamicListenerUriEncoding
{
    /// <summary>
    /// Scheme assigned to the dynamic-listener agent family. Used as both the
    /// dictionary key in <see cref="NodeAgentController"/> and the scheme of
    /// every <see cref="DynamicListenerAgent"/> URI.
    /// </summary>
    public const string SchemeName = "wolverine-dynamic-listener";

    public static Uri ToAgentUri(Uri listenerUri)
    {
        if (listenerUri is null) throw new ArgumentNullException(nameof(listenerUri));

        var encoded = Uri.EscapeDataString(listenerUri.ToString());
        return new Uri($"{SchemeName}:///{encoded}");
    }

    public static Uri ToListenerUri(Uri agentUri)
    {
        if (agentUri is null) throw new ArgumentNullException(nameof(agentUri));

        if (!string.Equals(agentUri.Scheme, SchemeName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Expected an agent URI with scheme '{SchemeName}' but got '{agentUri.Scheme}'",
                nameof(agentUri));
        }

        var path = agentUri.AbsolutePath.TrimStart('/');
        if (path.Length == 0)
        {
            throw new ArgumentException(
                $"Agent URI '{agentUri}' has no encoded listener URI in its path",
                nameof(agentUri));
        }

        var listenerString = Uri.UnescapeDataString(path);
        return new Uri(listenerString);
    }
}
