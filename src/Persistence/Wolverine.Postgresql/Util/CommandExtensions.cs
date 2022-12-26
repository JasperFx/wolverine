using System.Data.Common;
using JasperFx.Core.Reflection;
using Npgsql;
using NpgsqlTypes;

namespace Wolverine.Postgresql.Util;

public static class CommandExtensions
{
    internal static DbCommand WithEnvelopeIds(this DbCommand command, string name, Envelope[] envelopes)
    {
        var parameter = command.CreateParameter().As<NpgsqlParameter>();
        parameter.ParameterName = name;
        parameter.Value = envelopes.Select(x => x.Id).ToArray();
        // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
        parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid;
        command.Parameters.Add(parameter);

        return command;
    }
}