using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;

namespace Wolverine.Persistence;

/// <summary>
///     Picks the single persistence provider that owns a chain's transaction when more than one of them
///     <see cref="IPersistenceFrameProvider.CanApply" />. GH-4631.
/// </summary>
/// <remarks>
///     <para>
///         A handler that depends on both an EF Core <c>DbContext</c> and a Marten / Polecat / Fisher
///         <c>IDocumentSession</c> has two candidates and no way for Wolverine to know which one it meant.
///         The shipped behavior was to apply nothing at all: no <c>SaveChangesAsync</c> on either store, no
///         log line, no failure, and whatever both stores buffered discarded at scope end. An explicit
///         designation resolves it; the absence of one is an error, not a default.
///     </para>
///     <para>
///         One chain still gets one transaction from one provider — the designation says whose writes
///         commit, it does not span a transaction across two stores.
///     </para>
/// </remarks>
internal static class TransactionOwnerResolution
{
    /// <summary>
    ///     The designated owner among <paramref name="potentials" />, or an exception naming every
    ///     candidate and the designation to add.
    /// </summary>
    public static IPersistenceFrameProvider SelectDesignatedOwner(IChain chain,
        IReadOnlyList<IPersistenceFrameProvider> potentials, IServiceContainer container)
    {
        var designated = designatedStorageType(chain);
        if (designated != null)
        {
            var matches = potentials.Where(x => x.OwnsStorageType(designated, container)).ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }
        }

        throw new InvalidOperationException(buildMessage(chain, potentials, designated));
    }

    /// <summary>
    ///     The storage type designated for this chain by <c>[Transactional(typeof(X))]</c> or
    ///     <c>[Storage(typeof(X))]</c>, or null when there is no designation.
    /// </summary>
    private static Type? designatedStorageType(IChain chain)
    {
        if (chain.Tags.TryGetValue(TransactionalAttribute.TransactionalDbContextTypeKey, out var tagged)
            && tagged is Type taggedType)
        {
            return taggedType;
        }

        // Not IChain.AncillaryStoreType directly -- on an HTTP chain, and on a handler chain before the
        // eager policies have run, that is still null while the attribute is right there on the handler.
        return chain.DetermineAncillaryStoreType();
    }

    private static string buildMessage(IChain chain, IReadOnlyList<IPersistenceFrameProvider> potentials,
        Type? designated)
    {
        var names = potentials.Select(x => x.GetType().Name).Join(", ");

        var message =
            $"Cannot determine which persistence provider owns the transaction for {chain.Description}, multiple providers can apply: {names}. ";

        if (designated != null)
        {
            message +=
                $"The designated storage type '{designated.FullNameInCode()}' is not owned by any of them. ";
        }

        return message +
               "Wolverine will not guess, and applying none of them would silently discard everything the other stores buffered. " +
               "Either designate the owner with [Transactional(typeof(YourStorageType))] or [Storage(typeof(YourStore))] on the handler, " +
               "or remove the transactional middleware from this handler with [NonTransactional]. " +
               "Note that only the designated store commits -- Wolverine does not span one transaction across two stores.";
    }
}
