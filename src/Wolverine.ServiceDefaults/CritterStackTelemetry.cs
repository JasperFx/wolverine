namespace Wolverine.ServiceDefaults;

/// <summary>
/// The <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/> names
/// the Critter Stack publishes, as the wildcard patterns OpenTelemetry's <c>AddSource()</c> and <c>AddMeter()</c>
/// accept. Use these if you configure OpenTelemetry yourself instead of calling
/// <c>AddCritterStackServiceDefaults()</c>.
/// </summary>
public static class CritterStackTelemetry
{
    /// <summary>
    /// The ActivitySource name patterns:
    /// <list type="bullet">
    ///   <item><description><c>Wolverine*</c>: Wolverine's <c>Wolverine</c> source for sending, receiving and handling.</description></item>
    ///   <item><description><c>Marten*</c>, <c>Polecat*</c>, <c>Fisher*</c>: each store's own source (session, connection and daemon spans).</description></item>
    ///   <item><description><c>CritterWatch*</c>: the CritterWatch console's own sources.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ActivitySources { get; } =
        ["Wolverine*", "Marten*", "Polecat*", "Fisher*", "CritterWatch*"];

    /// <summary>
    /// The Meter name patterns. Wolverine's meter is named <c>Wolverine:{ServiceName}</c>, so a bare
    /// <c>AddMeter("Wolverine")</c> matches nothing: the wildcard is required. The CritterWatch client
    /// publishes on <c>Wolverine.CritterWatch</c> (matched by <c>Wolverine*</c>) and the console on
    /// <c>CritterWatch.Console</c> and <c>CritterWatch:*</c> meters.
    /// </summary>
    public static IReadOnlyList<string> Meters { get; } =
        ["Wolverine*", "Marten*", "Polecat*", "Fisher*", "CritterWatch*"];
}
