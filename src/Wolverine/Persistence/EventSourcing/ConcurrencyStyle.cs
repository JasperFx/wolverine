namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     How the aggregate handler workflow protects the aggregate's stream against concurrent writes.
/// </summary>
/// <remarks>
///     GH-3907: the store-agnostic form of what was <c>Wolverine.Marten.ConcurrencyStyle</c> and
///     <c>Wolverine.Polecat.ConcurrencyStyle</c>. Both of those remain, and remain the type of each
///     store attribute's <c>LoadStyle</c> property, because they are public enums that existing
///     handler code names directly — see the note on <c>WriteAggregateAttribute.LoadStyle</c> in each
///     integration for why that one property is the only thing keeping those shells from being empty.
/// </remarks>
public enum ConcurrencyStyle
{
    /// <summary>
    ///     Check for concurrency violations optimistically at the point of committing the updated data
    /// </summary>
    Optimistic,

    /// <summary>
    ///     Try to attain an exclusive lock on the data behind the current aggregate
    /// </summary>
    Exclusive
}
