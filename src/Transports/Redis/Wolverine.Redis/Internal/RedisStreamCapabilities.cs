using StackExchange.Redis;

namespace Wolverine.Redis.Internal;

/// <summary>
///     GH-4058. Server-side capability checks for the Redis stream transport.
/// </summary>
/// <remarks>
///     These ask the server what it actually implements rather than comparing <c>INFO server</c> version
///     numbers. Wolverine runs against plenty of things that speak the Redis protocol but do not carry
///     Redis's own version line -- Valkey, DragonflyDB, Memurai, and the managed AWS/Azure/GCP flavors all
///     report their own numbering -- so a <c>Version &gt;= 8.2</c> comparison would both reject servers that
///     do support the command and admit servers that do not.
/// </remarks>
internal static class RedisStreamCapabilities
{
    /// <summary>
    ///     The command behind <c>IDatabaseAsync.StreamAcknowledgeAndDeleteAsync</c>, which is how
    ///     <c>DeleteStreamEntryOnAck(true)</c> settles an entry. Added in Redis 8.2.
    /// </summary>
    internal const string XackDel = "XACKDEL";

    /// <summary>
    ///     Ask the server whether it implements <c>XACKDEL</c> via <c>COMMAND INFO</c>. A server that does not
    ///     know the command answers with a one-element array holding a nil, which is how this tells the two
    ///     apart.
    /// </summary>
    /// <returns>
    ///     <c>true</c> or <c>false</c> when the server gave a usable answer, and <c>null</c> when the probe
    ///     itself could not be run -- some managed offerings rename or ACL-restrict <c>COMMAND</c>, and an
    ///     inconclusive probe is not grounds for refusing to start.
    /// </returns>
    internal static async Task<bool?> SupportsXackDelAsync(IDatabaseAsync db)
    {
        try
        {
            var result = await db.ExecuteAsync("COMMAND", "INFO", XackDel);

            if (result.IsNull || result.Resp2Type != ResultType.Array || result.Length == 0)
            {
                return null;
            }

            return !result[0].IsNull;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     True when Redis rejected a command outright as unknown -- i.e. the server is too old for the command
    ///     Wolverine just issued, which no amount of retrying will fix.
    /// </summary>
    internal static bool IsUnknownCommandFailure(Exception ex)
    {
        return ex is RedisServerException &&
               ex.Message.Contains("unknown command", StringComparison.OrdinalIgnoreCase);
    }
}
