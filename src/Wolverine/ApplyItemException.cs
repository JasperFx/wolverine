namespace Wolverine;

/// <summary>
/// Disposition for the non-poison ("surviving") members of a batch when a batch handler throws an
/// <see cref="ApplyItemException"/> to isolate poison items.
/// </summary>
public enum NonPoisonItems
{
    /// <summary>Acknowledge every surviving item as-is; do not re-run any of them.</summary>
    AckAll,

    /// <summary>Re-run the batch handler over every surviving item as a fresh, reduced batch.</summary>
    Replay,

    /// <summary>
    /// Acknowledge the explicitly listed <see cref="ApplyItemException.AckItems"/> and replay the
    /// remaining survivors.
    /// </summary>
    AckSelected
}

/// <summary>
/// Thrown from a batch message handler that already knows which item(s) in the batch are "poison"
/// (deterministically bad) to isolate them from the healthy members. Wolverine dead-letters only the
/// poison items and dispositions the survivors per <see cref="Disposition"/>, so a single bad message
/// no longer dead-letters the whole batch. This mirrors JasperFx.Events' <c>ApplyEventException</c> /
/// <c>SkipApplyErrors</c>, where the daemon skips the offending event, dead-letters it, and rebuilds the
/// batch without it so the good events still commit. Construct via the static factories so the intent
/// reads clearly at the call site; there is no other opt-in — throwing the exception IS the opt-in.
/// </summary>
public sealed class ApplyItemException : Exception
{
    private ApplyItemException(IReadOnlyList<object> poisonItems, NonPoisonItems disposition,
        IReadOnlyList<object> ackItems, Exception? inner)
        : base(buildMessage(poisonItems, disposition), inner)
    {
        PoisonItems = poisonItems;
        Disposition = disposition;
        AckItems = ackItems;
    }

    /// <summary>The item(s) to dead-letter. Never empty.</summary>
    public IReadOnlyList<object> PoisonItems { get; }

    /// <summary>What to do with the surviving (non-poison) items.</summary>
    public NonPoisonItems Disposition { get; }

    /// <summary>
    /// The survivors to acknowledge as-is. Consulted only when <see cref="Disposition"/> is
    /// <see cref="NonPoisonItems.AckSelected"/>; the remaining survivors are replayed.
    /// </summary>
    public IReadOnlyList<object> AckItems { get; }

    /// <summary>
    /// Dead-letter the poison item(s) and re-run the batch handler over all the remaining items.
    /// </summary>
    public static ApplyItemException DeadLetterAndReplayOthers(params object[] poison)
    {
        return new ApplyItemException(requireNonEmpty(poison), NonPoisonItems.Replay,
            Array.Empty<object>(), null);
    }

    /// <summary>
    /// Dead-letter a single poison item (recording the underlying exception) and re-run the batch
    /// handler over all the remaining items. Mirrors <c>ApplyEventException(@event, inner)</c>.
    /// </summary>
    public static ApplyItemException DeadLetterAndReplayOthers(object poison, Exception inner)
    {
        return new ApplyItemException(requireNonEmpty(new[] { poison }), NonPoisonItems.Replay,
            Array.Empty<object>(), inner);
    }

    /// <summary>
    /// Dead-letter the poison item(s) and acknowledge all the remaining items as-is (no re-run). Use
    /// this when the batch handler already committed the good items in the same transaction.
    /// </summary>
    public static ApplyItemException DeadLetterAndAckOthers(params object[] poison)
    {
        return new ApplyItemException(requireNonEmpty(poison), NonPoisonItems.AckAll,
            Array.Empty<object>(), null);
    }

    /// <summary>
    /// Dead-letter the poison item(s), acknowledge the explicitly listed <paramref name="ackItems"/>
    /// as-is, and replay the remaining survivors.
    /// </summary>
    public static ApplyItemException DeadLetter(object[] poison, object[] ackItems)
    {
        return new ApplyItemException(requireNonEmpty(poison), NonPoisonItems.AckSelected,
            ackItems ?? Array.Empty<object>(), null);
    }

    private static object[] requireNonEmpty(object[] poison)
    {
        if (poison == null || poison.Length == 0)
        {
            throw new ArgumentException("At least one poison item is required", nameof(poison));
        }

        foreach (var item in poison)
        {
            if (item is null)
            {
                throw new ArgumentException("Poison items cannot be null", nameof(poison));
            }
        }

        return poison;
    }

    private static string buildMessage(IReadOnlyList<object> poisonItems, NonPoisonItems disposition)
    {
        var count = poisonItems?.Count ?? 0;
        return $"Isolating {count} poison item(s) from the batch (surviving items: {disposition}).";
    }
}
