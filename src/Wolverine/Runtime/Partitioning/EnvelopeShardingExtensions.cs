using JasperFx.Core;

namespace Wolverine.Runtime.Partitioning;

internal static class EnvelopeShardingExtensions
{
    internal static void AssertIsValidNumberOfProcessingSlots(this int slots)
    {
        int[] validValues = [3, 5, 7, 9];
        if (!validValues.Contains(slots))
        {
            throw new ArgumentOutOfRangeException(
                $"Invalid number of processing slots. The acceptable values are {validValues.Select(x => x.ToString()).Join(", ")}");
        }
    }
    
    /// <summary>
    /// Uses a combination of message grouping id rules and a deterministic hash
    /// to predictably assign envelopes to a slot to help "shard" message publishing.
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="numberOfSlots"></param>
    /// <param name="rules"></param>
    /// <returns></returns>
    public static int SlotForSending(this Envelope envelope, int numberOfSlots, MessagePartitioningRules rules)
    {
        var groupId = rules.DetermineGroupId(envelope);
        
        // Pick one at random, and has to be zero based
        if (groupId == null) return Random.Shared.Next(1, numberOfSlots) - 1;

        return Math.Abs(groupId.GetDeterministicHashCode() % numberOfSlots);
    }
    
    /// <summary>
    /// Uses a combination of message grouping id rules and a deterministic hash
    /// to predictably assign envelopes to a slot to help "shard" message processing
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="numberOfSlots">Number of slots. Valid values are 3, 5, 7, or 9</param>
    /// <param name="rules"></param>
    /// <returns></returns>
    public static int SlotForProcessing(this Envelope envelope, int numberOfSlots, MessagePartitioningRules rules)
    {
        var groupId = rules.DetermineGroupId(envelope);
        
        // Pick one at random, and has to be zero based
        if (groupId == null) return Random.Shared.Next(1, numberOfSlots) - 1;

        var slot = Math.Abs(groupId.DeterministicJavaCompliantHash() % numberOfSlots);

        return slot;
    }
}