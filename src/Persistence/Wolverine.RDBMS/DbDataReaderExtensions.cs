using System.Data.Common;

namespace Wolverine.RDBMS;

/// <summary>
/// Reads that have to survive whatever numeric type a provider chooses to surface.
/// </summary>
public static class DbDataReaderExtensions
{
    /// <summary>
    /// Read a numeric column as an <c>int</c>, tolerating whatever numeric type the provider surfaced for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetFieldValueAsync&lt;int&gt;()</c> is a CAST, not a conversion: it throws
    /// <c>InvalidCastException</c> unless the provider's own CLR mapping for that column is exactly
    /// <c>Int32</c>. That mapping is the provider's choice, and Oracle's in particular is not
    /// <c>Int32</c> -- an unconstrained <c>NUMBER</c>, which is what <c>count(*)</c> and the node-number
    /// columns produce, arrives from ODP.NET as <c>Int64</c> or <c>decimal</c>.
    /// </para>
    ///
    /// <para>
    /// This has now bitten twice in the same folder. GH-3971 (f71d5d6cc) found it in
    /// <see cref="Durability.ReleaseOrphanedMessagesCommand" />, where both reads sat inside a
    /// try/catch that logged and returned -- so on Oracle the whole orphaned-message sweep degraded to
    /// a silent no-op that released nothing, forever, while looking healthy apart from one log line per
    /// cycle. GH-4480 found it a month later in
    /// <see cref="Durability.CheckRecoverableIncomingMessagesOperation" />, where it stopped the
    /// durability agent recovering incoming messages at all. Each was fixed at its own call site, with a
    /// different idiom, which is why there was a second one to find.
    /// </para>
    ///
    /// <para>
    /// Prefer this over <c>GetFieldValueAsync&lt;int&gt;()</c> for any column a provider may widen:
    /// aggregates (<c>count(*)</c>, <c>sum</c>) above all, since those carry no declared precision for
    /// the provider to narrow against.
    /// </para>
    /// </remarks>
    public static async Task<int> GetInt32TolerantlyAsync(this DbDataReader reader, int index,
        CancellationToken token = default)
    {
        if (await reader.IsDBNullAsync(index, token).ConfigureAwait(false))
        {
            return 0;
        }

        // GetFieldValueAsync<object> rather than GetValue() so a provider that streams this column is
        // still awaited rather than blocked on.
        return Convert.ToInt32(await reader.GetFieldValueAsync<object>(index, token).ConfigureAwait(false));
    }
}
