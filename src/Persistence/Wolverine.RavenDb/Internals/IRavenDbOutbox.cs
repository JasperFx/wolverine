using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals;

/// <summary>
///     Outbox-ed messaging sending with RavenDb
/// </summary>
public interface IRavenDbOutbox : IMessageBus
{
    /// <summary>
    ///     Current document session
    /// </summary>
    IAsyncDocumentSession Session { get; }

    /// <summary>
    ///     Enroll a RavenDb document session into the outbox'd sender
    /// </summary>
    /// <param name="session"></param>
    void Enroll(IAsyncDocumentSession session);
}