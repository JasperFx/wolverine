using System.Reflection;
using JasperFx.Core;
using Weasel.Core;
using Weasel.SqlServer.Procedures;
using Wolverine.RDBMS;

namespace Wolverine.SqlServer.Schema;

internal class WolverineStoredProcedure : StoredProcedure
{
    public WolverineStoredProcedure(string fileName, IMessageDatabase database) : base(
        new DbObjectName(database.SchemaName, Path.GetFileNameWithoutExtension(fileName)), ReadText(database, fileName))
    {
    }

    internal static string ReadText(IMessageDatabase wolverineDatabase, string fileName)
    {
        return Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(typeof(WolverineStoredProcedure), fileName)!
            .ReadAllText().Replace("%SCHEMA%", wolverineDatabase.SchemaName);
    }
}