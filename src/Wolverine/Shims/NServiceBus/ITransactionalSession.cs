namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible transactional session interface.
/// Extends <see cref="IUniformSession"/> with transaction lifecycle.
/// Wolverine handles transactional messaging automatically, so the Open/Commit
/// methods are obsolete and will throw <see cref="NotSupportedException"/>.
/// </summary>
public interface ITransactionalSession : IUniformSession
{
    /// <summary>
    /// Opens the transactional session.
    /// </summary>
    [Obsolete("Wolverine handles transactions automatically. Delete this usage.")]
    Task Open();

    /// <summary>
    /// Commits the transactional session.
    /// </summary>
    [Obsolete("Wolverine handles transactions automatically. Delete this usage.")]
    Task Commit();
}
