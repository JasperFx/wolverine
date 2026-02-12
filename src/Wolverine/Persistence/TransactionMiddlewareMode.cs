namespace Wolverine.Persistence;

public enum TransactionMiddlewareMode
{
    /// <summary>
    /// Start a native database transaction immediately when starting the message handling or HTTP request handling.
    /// Use this for tools like EF Core that may require an explicit transaction for bulk operations
    ///
    /// Not supported or necessary for Marten or RavenDb
    /// </summary>
    Eager,
    
    /// <summary>
    /// Only rely on the underlying persistence tool's version of SaveChangesAsync() for transactional boundaries
    /// </summary>
    Lightweight
}