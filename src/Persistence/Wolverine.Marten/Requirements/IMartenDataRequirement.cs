using JasperFx.Core.Reflection;
using Marten;
using Marten.Services.BatchQuerying;
using Microsoft.Extensions.Logging;

namespace Wolverine.Marten.Requirements;

public interface IMartenDataRequirement
{
    Task<RequirementResult> CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation);
    Task<RequirementResult> CheckFromBatch(ILogger logger);
    void RegisterInBatch(IBatchedQuery query);
}

public class DocumentExists<TDoc, TId> : IMartenDataRequirement
{
    private readonly TId _identity;
    private readonly string _missingMessage;

    public DocumentExists(TId identity) : this(identity, $"No {typeof(TDoc).NameInCode()} with identity {identity} exists")
    {
    }

    public DocumentExists(TId identity, string missingMessage)
    {
        _identity = identity;
        _missingMessage = missingMessage;
    }

    public async Task<RequirementResult> CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation)
    {
        //var exists = await session
        throw new NotImplementedException();
    }

    public Task<RequirementResult> CheckFromBatch(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public void RegisterInBatch(IBatchedQuery query)
    {
        throw new NotImplementedException();
    }
}