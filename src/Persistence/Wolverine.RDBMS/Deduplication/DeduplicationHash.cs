using System.Security.Cryptography;
using System.Text;

namespace Wolverine.RDBMS.Deduplication;

/// <summary>
/// GH-4757. The binary comparison key for a logical deduplication id under
/// <see cref="MessageDeduplicationMode.CompareByHash" />.
///
/// <para>
/// Computed here, in the application, and never in SQL — which is the one consequential thing about
/// it. Every writer of <c>wolverine_deduplication</c> has to agree on the function, so there is
/// exactly one of it: the portable <c>RdbmsDeduplicationStore</c>, and the hand-written INSERTs that
/// ride a Marten, Polecat or Fisher transaction, all call this.
/// </para>
///
/// <para>
/// SHA-256 of the UTF-8 bytes. Not a cryptographic requirement — nothing here is a secret — but a
/// collision requirement, and it is the one place where "near enough" is not: two distinct ids that
/// hashed alike would have the second one refused as a duplicate of the first, silently, forever.
/// 32 bytes of SHA-256 makes that unreachable; a 64-bit hash would not.
/// </para>
/// </summary>
public static class DeduplicationHash
{
    /// <summary>
    /// The 32-byte comparison key for <paramref name="deduplicationId" />.
    /// </summary>
    public static byte[] For(string deduplicationId)
    {
        if (deduplicationId == null) throw new ArgumentNullException(nameof(deduplicationId));

        return SHA256.HashData(Encoding.UTF8.GetBytes(deduplicationId));
    }

    /// <summary>
    /// <see cref="For" /> rendered as a lower-case hex literal body, for the hand-written SQL on stores
    /// whose parameter binding does not reach a <c>byte[]</c> — see the Polecat and Fisher existence
    /// checks, which prefix this with <c>0x</c> and wrap it in <c>x'...'</c> respectively.
    /// </summary>
    public static string HexFor(string deduplicationId)
    {
        return Convert.ToHexStringLower(For(deduplicationId));
    }
}
