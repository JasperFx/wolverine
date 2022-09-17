using System.Data.Common;
using System.Linq;
using Baseline;
using Npgsql;
using NpgsqlTypes;

namespace Wolverine.Persistence.Postgresql.Util;

public static class CommandExtensions
{
    public static DbCommand WithEnvelopeIds(this DbCommand command, string name, Envelope[] envelopes)
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
