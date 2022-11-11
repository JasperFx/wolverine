using System;
using System.Collections.Generic;
using System.IO;
using Weasel.Core;
using Weasel.Core.Migrations;

namespace Wolverine.RDBMS;

public abstract partial class DatabaseBackedMessageStore<T> : IFeatureSchema
{
    void IFeatureSchema.WritePermissions(Migrator rules, TextWriter writer)
    {
        // Nothing
    }

    IEnumerable<Type> IFeatureSchema.DependentTypes()
    {
        yield break;
    }

    public abstract ISchemaObject[] Objects { get; }

    Type IFeatureSchema.StorageType => GetType();

    public override IFeatureSchema[] BuildFeatureSchemas()
    {
        return new IFeatureSchema[] { this };
    }
}
