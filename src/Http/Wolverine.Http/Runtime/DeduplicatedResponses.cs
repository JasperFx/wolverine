using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Http.Runtime;

/// <summary>
/// GH-4742. What a <c>[DeduplicatedWithResponse]</c> endpoint's generated code calls to claim its key, record
/// the response and release a failed claim, against the message store's
/// <see cref="IReplayableDeduplicationStore" />.
///
/// <para>
/// The store never sees the scoped key, only its SHA-256: the key carries the user and tenant verbatim, and a
/// case- or accent-insensitive collation (the SQL Server and MySQL defaults) would otherwise let "Han" be
/// answered with "han"'s response.
/// </para>
///
/// <para>
/// None of these take the request's cancellation token. A claim cancelled after the INSERT committed would be
/// orphaned, and the caller that hung up is exactly the one that will retry.
/// </para>
/// </summary>
public sealed class DeduplicatedResponses
{
    // The winner of a lost claim can be released before its row is read; retried this often, then refused.
    private const int ClaimAttempts = 3;

    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<DeduplicatedResponses> _logger;

    public DeduplicatedResponses(IWolverineRuntime runtime, ILogger<DeduplicatedResponses> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Null when this request won the claim, or has no key to claim. Otherwise the claim it lost to.
    /// </summary>
    public async ValueTask<DeduplicatedResponseClaim?> TryClaimAsync(string? deduplicationId, string? fingerprint,
        TimeSpan? window, Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return null;

        var store = storeFor(ancillaryStoreMarker);
        var id = StorageIdFor(deduplicationId);

        // Stored rather than computed at read time, as for [Deduplicated].
        var expires = DateTimeOffset.UtcNow.Add(window ?? _runtime.Options.Durability.DeduplicationWindow);

        for (var attempt = 0; attempt < ClaimAttempts; attempt++)
        {
            if (await store.TryClaimAsync(id, fingerprint!, expires).ConfigureAwait(false)) return null;

            var winner = await store.FindAsync(id).ConfigureAwait(false);
            if (winner != null)
            {
                _logger.LogInformation(
                    "Answering a repeat of deduplicated id '{DeduplicationId}' from its first request's claim",
                    deduplicationId);
                return winner;
            }
        }

        // Still churning: refuse as unanswered rather than run unclaimed.
        return new DeduplicatedResponseClaim(fingerprint!, null);
    }

    /// <summary>Record the response on the claim. Null records nothing.</summary>
    public async ValueTask RecordResponseAsync(string? deduplicationId, DeduplicatedResponse? response,
        Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId) || response is null) return;

        try
        {
            if (!await storeFor(ancillaryStoreMarker).RecordResponseAsync(StorageIdFor(deduplicationId), response)
                    .ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "The claim on deduplicated id '{DeduplicationId}' expired or was answered before its response could be recorded",
                    deduplicationId);
            }
        }
        catch (Exception e)
        {
            // Don't fail completed work: the claim stays unanswered and repeats get 409 until it expires.
            _logger.LogError(e,
                "Failed to record the response for deduplicated id '{DeduplicationId}'. Repeats will be refused with 409 rather than replayed until the claim expires",
                deduplicationId);
        }
    }

    /// <summary>Release the claim if no response was recorded on it.</summary>
    public async ValueTask ReleaseUnansweredAsync(string? deduplicationId, Type? ancillaryStoreMarker)
    {
        if (string.IsNullOrWhiteSpace(deduplicationId)) return;

        try
        {
            await storeFor(ancillaryStoreMarker).ReleaseUnansweredAsync(StorageIdFor(deduplicationId))
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Never mask the failure being unwound.
            _logger.LogError(e,
                "Failed to release the claim on deduplicated id '{DeduplicationId}' after a failed request. Retries will be refused with 409 until the claim expires",
                deduplicationId);
        }
    }

    /// <summary>The id the store keys a scoped key by.</summary>
    public static string StorageIdFor(string deduplicationId)
        => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deduplicationId)));

    private IReplayableDeduplicationStore storeFor(Type? ancillaryStoreMarker)
    {
        var store = ancillaryStoreMarker == null
            ? mainStoreOf(_runtime)
            : _runtime.Stores.FindAncillaryStore(ancillaryStoreMarker);

        return store switch
        {
            IReplayableDeduplicationStore { Enabled: true } replayable => replayable,
            IReplayableDeduplicationStore => throw new InvalidOperationException(
                $"[DeduplicatedWithResponse] needs {nameof(DurabilitySettings)}.{nameof(DurabilitySettings.EnableDeduplicatedResponses)} = true. See GH-4742"),
            _ => throw new InvalidOperationException(
                $"[DeduplicatedWithResponse] needs a message store that implements {nameof(IReplayableDeduplicationStore)}, but this one is {store.GetType().Name}. Use the PostgreSQL, SQL Server, MySQL or SQLite message store. See GH-4742")
        };
    }

    // With a database per tenant, claims live in the main database; a Tenant scope still keeps them apart.
    private static IMessageStore mainStoreOf(IWolverineRuntime runtime)
        => runtime.Storage is MultiTenantedMessageStore tenanted ? tenanted.Main : runtime.Storage;

    /// <summary>
    /// Why the host's message store cannot back <c>[DeduplicatedWithResponse]</c>, or null when it can. For the
    /// startup warning.
    /// </summary>
    internal static string? WhyUnsupported(IWolverineRuntime runtime)
        => mainStoreOf(runtime) switch
        {
            IReplayableDeduplicationStore { Enabled: true } => null,
            IReplayableDeduplicationStore =>
                $"{nameof(DurabilitySettings)}.{nameof(DurabilitySettings.EnableDeduplicatedResponses)} is off",
            var store => $"the message store ({store.GetType().Name}) does not implement {nameof(IReplayableDeduplicationStore)}"
        };
}
