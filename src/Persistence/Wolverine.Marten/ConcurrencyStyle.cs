namespace Wolverine.Marten;

public enum ConcurrencyStyle
{
    /// <summary>
    /// Check for concurrency violations optimistically at the point of committing the updated data
    /// </summary>
    Optimistic,
    
    /// <summary>
    /// Try to attain an exclusive lock on the data behind the current aggregate
    /// </summary>
    Exclusive
}