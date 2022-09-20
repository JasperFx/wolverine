using System.Collections.Generic;
using Wolverine.Postgresql.Schema;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;

namespace Wolverine.Marten;

internal class MartenDatabaseSchemaFeature : FeatureSchemaBase
{
    private readonly string _schemaName;

    public MartenDatabaseSchemaFeature(string schemaName) : base("WolverineEnvelopes", new PostgresqlMigrator())
    {
        _schemaName = schemaName;
    }

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return new IncomingEnvelopeTable(_schemaName);
        yield return new OutgoingEnvelopeTable(_schemaName);
        yield return new DeadLettersTable(_schemaName);
    }
}
