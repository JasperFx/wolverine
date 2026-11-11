using MassTransit;
using MassTransit.NewIdProviders;

namespace Wolverine;

/// <summary>
/// The NewId generator behind <see cref="EnvelopeIdGeneration.NewId"/> and
/// <see cref="EnvelopeIdGeneration.NewIdSqlServerOrder"/>.
/// </summary>
/// <remarks>
/// The static <c>NewId</c> builds an id from the machine's network identity, a timestamp and a sequence each
/// process counts on its own, so two processes on one machine draw the same ids at the same moment -- one
/// node's cascaded message then collides with another node's in a shared inbox, and the inbox's duplicate
/// detection drops one of them. This generator mixes the process id in as well. It is Wolverine's own instance,
/// rather than <c>NewId.SetProcessIdProvider()</c>, so the ids anything else in the process draws from
/// <c>NewId</c> (MassTransit, application code) are left exactly as they were.
/// </remarks>
internal static class EnvelopeIds
{
    private static readonly Lazy<NewIdGenerator> _generator =
        new(() => Create(new DateTimeTickProvider(), new CurrentProcessIdProvider()));

    /// <summary>
    /// The same tick and worker id providers the static <c>NewId</c> uses, plus a process id
    /// </summary>
    internal static NewIdGenerator Create(ITickProvider ticks, IProcessIdProvider? processId) =>
        new(ticks, new BestPossibleWorkerIdProvider(), processId, 0);

    /// <summary>
    /// Ordered by time the way .NET and PostgreSQL compare GUIDs
    /// </summary>
    public static Guid NextSequentialGuid() => _generator.Value.NextSequentialGuid();

    /// <summary>
    /// Ordered by time the way SQL Server compares a <c>uniqueidentifier</c>
    /// </summary>
    public static Guid NextGuid() => _generator.Value.NextGuid();
}
