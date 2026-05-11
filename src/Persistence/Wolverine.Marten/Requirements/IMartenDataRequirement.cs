using JasperFx.Core.Reflection;
using Marten;
using Marten.Services.BatchQuerying;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence;

namespace Wolverine.Marten.Requirements;

public interface IMartenDataRequirement
{
    Task CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation);
    Task CheckFromBatch(ILogger logger);
    void RegisterInBatch(IBatchedQuery query);
}

/// <summary>
/// Returning this from a "Before/Validate" method in a handler or HTTP endpoint will opt into a declarative
/// check that the designated document exists in Marten
/// </summary>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public class DocumentExists<TDoc, TId> : IMartenDataRequirement
    where TDoc : class
    where TId: notnull
{
    private readonly TId _identity;
    private readonly string _missingMessage;
    private Task<bool>? _query;

    public DocumentExists(TId identity) : this(identity, $"No {typeof(TDoc).NameInCode()} with identity {identity} exists")
    {
    }

    public DocumentExists(TId identity, string missingMessage)
    {
        _identity = identity;
        _missingMessage = missingMessage;
    }

    public async Task CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation)
    {
        var exists = await session.CheckExistsAsync<TDoc>(_identity, cancellation);
        if (!exists)
        {
            logger.LogWarning("Marten data requirement failure: {Message}", _missingMessage);
            throw new RequiredDataMissingException(_missingMessage);
        }
    }

    public async Task CheckFromBatch(ILogger logger)
    {
        if (_query == null)
        {
            throw new InvalidOperationException("This method was called before registering in a batch query");
        }

        var exists = await _query;
        if (!exists)
        {
            logger.LogWarning("Marten data requirement failure: {Message}", _missingMessage);
            throw new RequiredDataMissingException(_missingMessage);
        }
    }

    public void RegisterInBatch(IBatchedQuery query)
    {
        _query = query.CheckExists<TDoc>(_identity);
    }
}

/// <summary>
/// Returning this from a "Before/Validate" method in a handler or HTTP endpoint will opt into a declarative
/// check that the designated document does not already exist in Marten
/// </summary>
/// <typeparam name="TDoc"></typeparam>
/// <typeparam name="TId"></typeparam>
public class DocumentDoesNotExist<TDoc, TId> : IMartenDataRequirement
    where TDoc : class
    where TId : notnull
{
    private readonly TId _identity;
    private readonly string _existsMessage;
    private Task<bool>? _query;

    public DocumentDoesNotExist(TId identity) : this(identity, $"A {typeof(TDoc).NameInCode()} with identity {identity} already exists")
    {
    }

    public DocumentDoesNotExist(TId identity, string existsMessage)
    {
        _identity = identity;
        _existsMessage = existsMessage;
    }

    public async Task CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation)
    {
        var exists = await session.CheckExistsAsync<TDoc>(_identity, cancellation);
        if (exists)
        {
            logger.LogWarning("Marten data requirement failure: {Message}", _existsMessage);
            throw new RequiredDataMissingException(_existsMessage);
        }
    }

    public async Task CheckFromBatch(ILogger logger)
    {
        if (_query == null)
        {
            throw new InvalidOperationException("This method was called before registering in a batch query");
        }

        var exists = await _query;
        if (exists)
        {
            logger.LogWarning("Marten data requirement failure: {Message}", _existsMessage);
            throw new RequiredDataMissingException(_existsMessage);
        }
    }

    public void RegisterInBatch(IBatchedQuery query)
    {
        _query = query.CheckExists<TDoc>(_identity);
    }
}
