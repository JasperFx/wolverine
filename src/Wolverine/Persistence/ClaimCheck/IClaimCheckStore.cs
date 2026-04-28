namespace Wolverine.Persistence;

/// <summary>
/// Abstraction for an external storage backend used by the Wolverine
/// Claim Check / DataBus pattern. Implementations persist large payloads
/// out-of-band from the message transport and return an opaque token
/// that travels with the message in the envelope headers.
/// </summary>
public interface IClaimCheckStore
{
    /// <summary>
    /// Persist <paramref name="payload"/> in the backing store and return
    /// the token that subsequent <see cref="LoadAsync"/> / <see cref="DeleteAsync"/>
    /// calls will use to refer back to it.
    /// </summary>
    Task<ClaimCheckToken> StoreAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the bytes previously stored under <paramref name="token"/>.
    /// </summary>
    Task<ReadOnlyMemory<byte>> LoadAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the payload referenced by <paramref name="token"/>. This is a
    /// best-effort delete; missing entries should not throw.
    /// </summary>
    Task DeleteAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default);
}
