using Marten;
using Marten.Services.BatchQuerying;
using Microsoft.Extensions.Logging;

namespace Wolverine.Marten.Requirements;

public interface IDataRequirement
{
    Task<RequirementResult> CheckAsync(IDocumentSession session, ILogger logger, CancellationToken cancellation);
    Task<RequirementResult> CheckFromBatch(ILogger logger);
    void RegisterInBatch(IBatchedQuery query);
}