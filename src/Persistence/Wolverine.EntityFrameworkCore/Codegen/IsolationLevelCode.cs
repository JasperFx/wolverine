using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     Renders the <c>Database.BeginTransactionAsync(...)</c> call for generated C# and F#, with the
///     optional <see cref="System.Data.IsolationLevel" />. With no level configured it is exactly the
///     call generated before the option existed. The isolation level overload is an extension method
///     (<see cref="RelationalDatabaseFacadeExtensions" />), so it is called statically and fully
///     qualified: generated code cannot count on <c>using Microsoft.EntityFrameworkCore</c>.
/// </summary>
internal static class IsolationLevelCode
{
    private static readonly string _extensions = typeof(RelationalDatabaseFacadeExtensions).FullName!;

    public static string CSharpBeginTransaction(string dbContext, System.Data.IsolationLevel? level, string cancellation)
    {
        return level == null
            ? $"{dbContext}.Database.BeginTransactionAsync({cancellation})"
            : $"global::{_extensions}.BeginTransactionAsync({dbContext}.Database, global::System.Data.IsolationLevel.{level.Value}, {cancellation})";
    }

    public static string FSharpBeginTransaction(string dbContext, System.Data.IsolationLevel? level, string cancellation)
    {
        return level == null
            ? $"{dbContext}.Database.BeginTransactionAsync({cancellation})"
            : $"{_extensions}.BeginTransactionAsync({dbContext}.Database, System.Data.IsolationLevel.{level.Value}, {cancellation})";
    }
}
