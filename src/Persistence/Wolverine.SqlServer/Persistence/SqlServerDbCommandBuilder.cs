using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using DbCommandBuilder = Weasel.Core.DbCommandBuilder;

namespace Wolverine.SqlServer.Persistence;

/// <summary>
///     GH-5292. The <see cref="DbCommandBuilder" /> every SQL Server message store command is built with.
///     <para>
///     Weasel's generic builder types a parameter through a provider-neutral mapping that SqlClient reads badly in
///     two places. It has no entry for <see cref="Guid" />, so <c>AppendParameter(Guid)</c> sets
///     <see cref="DbType.Object" />, which SqlClient sends as <c>sql_variant</c>. Comparing that with the
///     <c>uniqueidentifier</c> id column converts the column on every row, so a <c>where id = @p</c> statement scans
///     the envelope table instead of seeking on its primary key. And it maps <see cref="DateTime" /> to
///     <see cref="DbType.Date" />, which SqlClient sends as <c>date</c> and so drops the time of day.
///     </para>
///     <para>
///     In both cases this builder leaves the parameter untyped and lets SqlClient infer the type from the value,
///     which is how the inbox and outbox inserts have always bound their values. This can go once weasel#758 and
///     weasel#759 are fixed.
///     </para>
/// </summary>
internal class SqlServerDbCommandBuilder : DbCommandBuilder
{
    public SqlServerDbCommandBuilder() : base(new SqlCommand())
    {
    }

    public SqlServerDbCommandBuilder(SqlConnection connection) : base(connection)
    {
    }

    public override DbParameter AddParameter(object? value, DbType? dbType = null)
    {
        return inferMistyped(base.AddParameter(value, dbType));
    }

    // An untyped value is typed through the same generic mapping by the base class, so this checks the result too
    public override DbParameter AddNamedParameter(string name, object value, DbType? dbType = null)
    {
        return inferMistyped(base.AddNamedParameter(name, value, dbType));
    }

    // A null value has nothing to infer from, so it keeps whatever type it was given
    private static DbParameter inferMistyped(DbParameter parameter)
    {
        if (parameter.Value is null or DBNull) return parameter;

        if (parameter.DbType == DbType.Object || (parameter.DbType == DbType.Date && parameter.Value is DateTime))
        {
            parameter.ResetDbType();
        }

        return parameter;
    }
}
