using System.Data.Common;
using Wolverine.Util;

namespace Wolverine.RDBMS;

internal static class ReaderExtensions
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