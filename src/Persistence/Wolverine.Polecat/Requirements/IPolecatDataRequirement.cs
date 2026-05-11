using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Polecat;
using Polecat.Batching;
using Wolverine.Persistence;

namespace Wolverine.Polecat.Requirements;

/// <summary>
/// Marker interface for declarative data requirement checks against a Polecat document store.
/// Implementations are returned from "Before" / "Validate" methods on a handler or HTTP endpoint
/// and are evaluated by Wolverine before the handler body runs. When two or more
/// <see cref="IPolecatDataRequirement"/> values participate on the same chain alongside other
/// batchable operations (e.g., <c>[ReadAggregate]</c>, <c>[WriteAggregate]</c>), Wolverine
/// combines their queries into a single Polecat <see cref="IBatchedQuery"/> round-trip.
/// </summary>
public interface IPolecatDataRequirement
{
    /// <summary>Evaluate the requirement directly against an <see cref="IDocumentSession"/> when no batch is in flight.</summary>
    Task CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation);

    /// <summary>Evaluate the result that was previously enlisted via <see cref="RegisterInBatch"/>.</summary>
    Task CheckFromBatch(ILogger logger);

    /// <summary>Enlist the requirement's existence check in a Polecat batch query.</summary>
    void RegisterInBatch(IBatchedQuery query);
}

/// <summary>
/// Returning this from a "Before" / "Validate" method opts into a declarative check that the
/// designated Polecat document exists. Throws <see cref="RequiredDataMissingException"/> when
/// the document is missing.
/// </summary>
public class DocumentExists<TDoc, TId> : IPolecatDataRequirement where TDoc : class
{
    private readonly TId _identity;
    private readonly string _missingMessage;
    private Task<bool>? _query;

    public DocumentExists(TId identity)
        : this(identity, $"No {typeof(TDoc).NameInCode()} with identity {identity} exists")
    {
    }

    public DocumentExists(TId identity, string missingMessage)
    {
        _identity = identity;
        _missingMessage = missingMessage;
    }

    public async Task CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation)
    {
        var exists = await CheckExistsAsync(session, _identity, cancellation);
        if (!exists)
        {
            logger.LogWarning("Polecat data requirement failure: {Message}", _missingMessage);
            throw new RequiredDataMissingException(_missingMessage);
        }
    }

    public async Task CheckFromBatch(ILogger logger)
    {
        if (_query == null)
        {
            throw new InvalidOperationException("This method was called before registering in a batch query");
        }

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        var exists = await _query;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
        if (!exists)
        {
            logger.LogWarning("Polecat data requirement failure: {Message}", _missingMessage);
            throw new RequiredDataMissingException(_missingMessage);
        }
    }

    public void RegisterInBatch(IBatchedQuery query)
    {
        _query = CheckExistsBatch(query, _identity);
    }

    internal static Task<bool> CheckExistsAsync(IQuerySession session, TId id, CancellationToken cancellation) =>
        id switch
        {
            Guid guidId => session.CheckExistsAsync<TDoc>(guidId, cancellation),
            string stringId => session.CheckExistsAsync<TDoc>(stringId, cancellation),
            int intId => session.CheckExistsAsync<TDoc>(intId, cancellation),
            long longId => session.CheckExistsAsync<TDoc>(longId, cancellation),
            _ => throw new NotSupportedException(UnsupportedIdMessage())
        };

    internal static Task<bool> CheckExistsBatch(IBatchedQuery query, TId id) =>
        id switch
        {
            Guid guidId => query.CheckExists<TDoc>(guidId),
            string stringId => query.CheckExists<TDoc>(stringId),
            int intId => query.CheckExists<TDoc>(intId),
            long longId => query.CheckExists<TDoc>(longId),
            _ => throw new NotSupportedException(UnsupportedIdMessage())
        };

    private static string UnsupportedIdMessage() =>
        $"Polecat does not support id type {typeof(TId).FullNameInCode()} for {typeof(TDoc).NameInCode()}. " +
        "Supported id types are System.Guid, System.String, System.Int32, and System.Int64.";
}

/// <summary>
/// Returning this from a "Before" / "Validate" method opts into a declarative check that the
/// designated Polecat document does NOT exist. Throws <see cref="RequiredDataMissingException"/>
/// when the document already exists.
/// </summary>
public class DocumentDoesNotExist<TDoc, TId> : IPolecatDataRequirement where TDoc : class
{
    private readonly TId _identity;
    private readonly string _existsMessage;
    private Task<bool>? _query;

    public DocumentDoesNotExist(TId identity)
        : this(identity, $"A {typeof(TDoc).NameInCode()} with identity {identity} already exists")
    {
    }

    public DocumentDoesNotExist(TId identity, string existsMessage)
    {
        _identity = identity;
        _existsMessage = existsMessage;
    }

    public async Task CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation)
    {
        var exists = await DocumentExists<TDoc, TId>.CheckExistsAsync(session, _identity, cancellation);
        if (exists)
        {
            logger.LogWarning("Polecat data requirement failure: {Message}", _existsMessage);
            throw new RequiredDataMissingException(_existsMessage);
        }
    }

    public async Task CheckFromBatch(ILogger logger)
    {
        if (_query == null)
        {
            throw new InvalidOperationException("This method was called before registering in a batch query");
        }

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        var exists = await _query;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
        if (exists)
        {
            logger.LogWarning("Polecat data requirement failure: {Message}", _existsMessage);
            throw new RequiredDataMissingException(_existsMessage);
        }
    }

    public void RegisterInBatch(IBatchedQuery query)
    {
        _query = DocumentExists<TDoc, TId>.CheckExistsBatch(query, _identity);
    }
}
