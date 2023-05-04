using Weasel.Core;
using Weasel.Core.Migrations;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : IFeatureSchema
{
    void IFeatureSchema.WritePermissions(Migrator rules, TextWriter writer)
    {
        // Nothing
    }

    IEnumerable<Type> IFeatureSchema.DependentTypes()
    {
        yield break;
    }

    public ISchemaObject[] Objects => AllObjects().ToArray();

    Type IFeatureSchema.StorageType => GetType();

    public override IFeatureSchema[] BuildFeatureSchemas()
    {
        return new IFeatureSchema[] { this };
    }
}