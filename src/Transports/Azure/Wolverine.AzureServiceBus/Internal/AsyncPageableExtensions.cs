using Azure;

namespace Wolverine.AzureServiceBus.Internal;

internal static class AsyncPageableExtensions
{
    public static async ValueTask<List<T>> ToListAsync<T>(this AsyncPageable<T> source)
        where T : notnull
    {
        var list = new List<T>();

        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}