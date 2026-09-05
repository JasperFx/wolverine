using Wolverine.Runtime.Agents;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-4280. The agent-URI round trip is covered on every provider that inherits the shared compliance
/// suites, but it was only ever driven with toy values -- <c>fake://1</c>, eight characters, and
/// <c>red://leader</c>. So a provider could declare an agent URI column as <c>varchar(100)</c> and stay
/// green across the entire cross-provider suite, which is exactly what SQL Server did for a release: the
/// defect in GH-4246 was found by reading a table definition, not by a test.
///
/// These are realistic agent and node URIs at the full <see cref="AgentUri.MaximumLength"/> width every
/// store has to support, so a narrow column now fails a test rather than an operator's production insert.
/// </summary>
public static class LongAgentUri
{
    /// <summary>
    /// An event subscription agent URI -- projection name plus tenant id -- padded out to exactly
    /// <see cref="AgentUri.MaximumLength"/> characters.
    /// </summary>
    public static Uri ForAgent() => Build("event-subscriptions://marten/", "@some-rather-long-tenant-identifier");

    /// <summary>
    /// A database-backed node control URI at the same width. <c>wolverine_nodes.uri</c> is bounded on
    /// every RDBMS provider too, and it holds one of these.
    /// </summary>
    public static Uri ForNode() => Build("wolverinedb://sqlserver/some-host-name/", "/wolverine_control_schema");

    /// <summary>
    /// Distinct long URIs, all of the same full width, for the tests that need more than one.
    /// </summary>
    public static Uri ForAgent(int index) =>
        Build("event-subscriptions://marten/", $"@some-rather-long-tenant-identifier-{index}");

    private static Uri Build(string prefix, string suffix)
    {
        var padding = AgentUri.MaximumLength - prefix.Length - suffix.Length;
        if (padding < 1)
        {
            throw new InvalidOperationException(
                $"'{prefix}' + '{suffix}' is already longer than {AgentUri.MaximumLength} characters");
        }

        // A single long, legal path segment. Deliberately not random: a compliance failure has to be
        // reproducible from the test name alone.
        var raw = prefix + new string('X', padding) + suffix;

        var uri = new Uri(raw);

        // The whole point is the *stored* length, so pin what the provider is actually handed. A Uri that
        // normalized itself shorter would quietly stop testing anything.
        if (uri.ToString().Length != AgentUri.MaximumLength)
        {
            throw new InvalidOperationException(
                $"Expected a URI of exactly {AgentUri.MaximumLength} characters, but '{uri}' is {uri.ToString().Length}");
        }

        return uri;
    }
}
