using System.Security.Cryptography;
using System.Text;

namespace Wolverine.RDBMS.Sagas;

/// <summary>
/// GH-5558. Index naming for the lightweight saga tables
/// </summary>
public static class SagaTableIndexes
{
    public const string LastModifiedColumn = "last_modified";

    /// <summary>
    /// The name of the index on <see cref="LastModifiedColumn"/> for a saga table, kept within the database's
    /// identifier limit. A name that would be too long keeps a readable prefix and ends with a short hash of the
    /// table name, so it stays unique and the same on every start (a changing name would rebuild the index)
    /// </summary>
    public static string LastModifiedIndexName(string tableName, int maxLength)
    {
        var name = $"idx_{tableName}_{LastModifiedColumn}";
        if (name.Length <= maxLength) return name;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tableName)))[..8].ToLowerInvariant();
        var suffix = $"_{hash}_lm";
        return name[..(maxLength - suffix.Length)] + suffix;
    }
}
