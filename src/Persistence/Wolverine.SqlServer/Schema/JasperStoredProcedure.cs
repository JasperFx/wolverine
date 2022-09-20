using System.IO;
using System.Reflection;
using Baseline;
using Wolverine.RDBMS;
using Weasel.Core;
using Weasel.SqlServer.Procedures;

namespace Wolverine.SqlServer.Schema;

internal class WolverineStoredProcedure : StoredProcedure
{
    public WolverineStoredProcedure(string fileName, DatabaseSettings settings) : base(
        new DbObjectName(settings.SchemaName, Path.GetFileNameWithoutExtension(fileName)), ReadText(settings, fileName))
    {
    }

    internal static string ReadText(DatabaseSettings databaseSettings, string fileName)
    {
        return Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(typeof(WolverineStoredProcedure), fileName)!
            .ReadAllText().Replace("%SCHEMA%", databaseSettings.SchemaName);
    }
}
