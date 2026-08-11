using System.Reflection;
using Wolverine.ComplianceTests;
using Wolverine.Marten;

namespace MartenTests;

// GH-3907 decision 2 — enrolls Wolverine.Marten in the shared invariant that no Wolverine core type
// may share a simple name with a public type in a store integration.
public class marten_core_type_name_collisions : CoreTypeNameCollisionCompliance
{
    protected override Assembly StoreAssembly => typeof(WriteAggregateAttribute).Assembly;
}
