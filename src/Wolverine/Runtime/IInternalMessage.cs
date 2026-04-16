namespace Wolverine.Runtime;

/// <summary>
/// Marker interface for message types that are internal to Wolverine or to extension
/// frameworks (such as CritterWatch). Types implementing this interface are excluded
/// from <see cref="Wolverine.Configuration.Capabilities.ServiceCapabilities"/> and
/// from <c>IWolverineObserver</c> notifications such as <c>MessageRouted</c> and
/// <c>MessageCausedBy</c>.
///
/// Use this for system commands and infrastructure messages that should not appear in
/// observability tooling alongside user-defined messages. Per-instance filtering uses
/// a single type-test (<c>x is IInternalMessage</c>), which is cheaper than reflection
/// or attribute lookups.
/// </summary>
public interface IInternalMessage;
