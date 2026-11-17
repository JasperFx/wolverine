using JasperFx.Events.Daemon;
using Wolverine.Persistence.EventSourcing.Distribution;

namespace Wolverine.Fisher.Distribution;

/// <summary>
///     GH-5094: the wording of Wolverine core's <see cref="ManagedDistributionDaemonModeValidator" /> refusal for a
///     Fisher store, whose only daemon hosting is <c>AddAsyncDaemon(DaemonMode.Solo)</c> (Fisher refuses HotCold).
/// </summary>
internal static class FisherDaemonConflict
{
    internal static string MessageFor(string storeDescription) =>
        ManagedDistributionDaemonModeValidator.MessageFor(storeDescription, DaemonMode.Solo,
            "Fisher's Solo daemon would project the same events into the same SQLite file as Wolverine's agents. Remove AddAsyncDaemon() (or use DaemonMode.ExternallyManaged)");
}
