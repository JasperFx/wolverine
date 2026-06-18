namespace Wolverine.Kafka.Internals;

/// <summary>
/// Resolves a Kafka <c>group.instance.id</c> for static group membership (GH-3139). The id must be
/// unique per node and stable across restarts of the same logical node, so it is sourced from an
/// explicit value or — by default — the environment conventions used by k8s StatefulSets.
/// </summary>
internal static class KafkaStaticMembership
{
    /// <summary>
    /// Resolution order: an explicitly supplied value, then <c>POD_NAME</c>, then <c>HOSTNAME</c>,
    /// then the machine name. Returns null only if every source is blank.
    /// </summary>
    public static string? Resolve(Func<string?>? source)
    {
        return Clean(source?.Invoke())
               ?? Clean(Environment.GetEnvironmentVariable("POD_NAME"))
               ?? Clean(Environment.GetEnvironmentVariable("HOSTNAME"))
               ?? Clean(Environment.MachineName);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
