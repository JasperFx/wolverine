using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Util;

namespace Wolverine.Persistence.Database;

public static class ReaderExtensions
{
    // TODO -- move this to Weasel
    public static async Task<Uri?> ReadUriAsync(this DbDataReader reader, int index,
        CancellationToken cancellation = default)
    {
        if (await reader.IsDBNullAsync(index, cancellation))
        {
            return default;
        }

        return (await reader.GetFieldValueAsync<string>(index, cancellation)).ToUri();
    }

    // TODO -- move this to Weasel
    public static async Task<T?> MaybeReadAsync<T>(this DbDataReader reader, int index,
        CancellationToken cancellation = default)
    {
        if (await reader.IsDBNullAsync(index, cancellation))
        {
            return default;
        }

        return await reader.GetFieldValueAsync<T>(index, cancellation);
    }
}
