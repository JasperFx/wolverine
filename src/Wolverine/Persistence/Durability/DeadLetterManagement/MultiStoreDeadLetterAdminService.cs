using JasperFx.Core;

namespace Wolverine.Persistence.Durability.DeadLetterManagement;

public class MultiStoreDeadLetterAdminService : IDeadLetterAdminService
{
    private ImHashMap<Uri, IDeadLetterAdminService> _databases = ImHashMap<Uri, IDeadLetterAdminService>.Empty;

    public void AddStore(IDeadLetterAdminService service)
    {
        _databases = _databases.AddOrUpdate(service.Uri, service);
    }

    private async Task executeOnAll(Func<IDeadLetterAdminService, Task> step)
    {
        foreach (var entry in _databases.Enumerate().OrderBy(x => x.Key).ToArray())
        {
            await step(entry.Value);
        }
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token)
    {
        var list = new List<DeadLetterQueueCount>();

        await executeOnAll(async service => list.AddRange(await service.SummarizeAllAsync(serviceName, range, token)));

        return list;
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeByDatabaseAsync(string serviceName, Uri database, TimeRange range, CancellationToken token)
    {
        if (_databases.TryFind(database, out var service))
        {
            return await service.SummarizeAllAsync(serviceName, range, token);
        }

        return [];
    }

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.Database != null)
        {
            if (_databases.TryFind(query.Database!, out var service))
            {
                return await service.QueryAsync(query, token);
            }
        }

        if (query.PageNumber > 0) throw new InvalidOperationException("Must specify a database to use paged querying");

        var results = new DeadLetterEnvelopeResults { PageNumber = 0 };
        await executeOnAll(async service =>
        {
            if (query.PageSize <= 0) return;
            
            var singleResults = await service.QueryAsync(query, token);
            results.TotalCount += singleResults.TotalCount;
            results.Envelopes.AddRange(singleResults.Envelopes);
            query.PageSize -= singleResults.Envelopes.Count;
        });

        return results;
    }

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.Database != null)
        {
            if (_databases.TryFind(query.Database!, out var service))
            {
                return service.DiscardAsync(query, token);
            }
        }

        return executeOnAll(service => service.DiscardAsync(query, token));
    }

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.Database != null)
        {
            if (_databases.TryFind(query.Database!, out var service))
            {
                return service.ReplayAsync(query, token);
            }
        }

        return executeOnAll(service => service.ReplayAsync(query, token));
    }

    public Task DiscardAsync(MessageBatchRequest request, CancellationToken token)
    {
        if (request.Database != null)
        {
            if (_databases.TryFind(request.Database!, out var service))
            {
                return service.DiscardAsync(request, token);
            }
        }

        return executeOnAll(service => service.DiscardAsync(request, token));
    }

    public Task ReplayAsync(MessageBatchRequest request, CancellationToken token)
    {
        if (request.Database != null)
        {
            if (_databases.TryFind(request.Database!, out var service))
            {
                return service.ReplayAsync(request, token);
            }
        }

        return executeOnAll(service => service.ReplayAsync(request, token));
    }

    public Uri Uri => new("wolverinedb://all");
}